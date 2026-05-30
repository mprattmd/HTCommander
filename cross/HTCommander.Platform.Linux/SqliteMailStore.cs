/*
Copyright 2026 Ylian Saint-Hilaire

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace HTCommander.Platform.Linux;

/// <summary>
/// Cross-platform <see cref="IMailStore"/> backed by <c>Microsoft.Data.Sqlite</c>
/// (bundles its own native SQLite, so it runs on Linux/macOS — unlike the WinForms
/// <c>System.Data.SQLite</c> store). Schema matches the original `mails`/`attachments`
/// tables so the Winlink mail layer (Core <see cref="WinLinkMail"/>) works unchanged.
/// Attachments are stored as files under an "attachments" subfolder.
/// </summary>
public sealed class SqliteMailStore : IMailStore
{
    private readonly SqliteConnection _connection;
    private readonly string _attachmentsPath;
    private readonly object _gate = new object();

    public event EventHandler? MailsChanged;   // single-instance store: not raised externally

    public SqliteMailStore(string storageDir)
    {
        Directory.CreateDirectory(storageDir);
        _attachmentsPath = Path.Combine(storageDir, "attachments");
        Directory.CreateDirectory(_attachmentsPath);

        _connection = new SqliteConnection($"Data Source={Path.Combine(storageDir, "mail.db")}");
        _connection.Open();
        Exec("PRAGMA foreign_keys = ON;");
        Exec(@"CREATE TABLE IF NOT EXISTS mails (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 mid TEXT UNIQUE NOT NULL,
                 datetime TEXT NOT NULL,
                 from_addr TEXT, to_addr TEXT, cc TEXT, subject TEXT, mbo TEXT,
                 body TEXT, tag TEXT, location TEXT,
                 flags INTEGER DEFAULT 0,
                 mailbox TEXT DEFAULT 'Inbox',
                 created_at TEXT DEFAULT CURRENT_TIMESTAMP);
               CREATE INDEX IF NOT EXISTS idx_mails_mid ON mails(mid);
               CREATE INDEX IF NOT EXISTS idx_mails_mailbox ON mails(mailbox);");
        Exec(@"CREATE TABLE IF NOT EXISTS attachments (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 mail_mid TEXT NOT NULL,
                 filename TEXT NOT NULL,
                 filepath TEXT NOT NULL,
                 FOREIGN KEY (mail_mid) REFERENCES mails(mid) ON DELETE CASCADE);
               CREATE INDEX IF NOT EXISTS idx_attachments_mail ON attachments(mail_mid);");
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM mails";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    public List<WinLinkMail> GetAllMails()
    {
        lock (_gate)
        {
            var mails = new List<WinLinkMail>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT mid, datetime, from_addr, to_addr, cc, subject, mbo, body, tag, location, flags, mailbox FROM mails";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) mails.Add(ReadMail(reader));
            }
            foreach (var m in mails) m.Attachments = LoadAttachments(m.MID);
            return mails;
        }
    }

    public WinLinkMail GetMail(string mid)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT mid, datetime, from_addr, to_addr, cc, subject, mbo, body, tag, location, flags, mailbox FROM mails WHERE mid = @mid";
            cmd.Parameters.AddWithValue("@mid", mid);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var mail = ReadMail(reader);
            mail.Attachments = LoadAttachments(mid);
            return mail;
        }
    }

    public bool MailExists(string mid)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM mails WHERE mid = @mid LIMIT 1";
            cmd.Parameters.AddWithValue("@mid", mid);
            return cmd.ExecuteScalar() != null;
        }
    }

    public void AddMail(WinLinkMail mail)
    {
        lock (_gate)
        {
            using (var tx = _connection.BeginTransaction())
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR REPLACE INTO mails
                        (mid, datetime, from_addr, to_addr, cc, subject, mbo, body, tag, location, flags, mailbox)
                        VALUES (@mid,@dt,@from,@to,@cc,@subj,@mbo,@body,@tag,@loc,@flags,@mailbox)";
                    BindMail(cmd, mail);
                    cmd.ExecuteNonQuery();
                }
                SaveAttachments(mail, tx);
                tx.Commit();
            }
            MailsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void UpdateMail(WinLinkMail mail)
    {
        lock (_gate)
        {
            using (var tx = _connection.BeginTransaction())
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE mails SET datetime=@dt, from_addr=@from, to_addr=@to, cc=@cc,
                        subject=@subj, mbo=@mbo, body=@body, tag=@tag, location=@loc, flags=@flags, mailbox=@mailbox
                        WHERE mid=@mid";
                    BindMail(cmd, mail);
                    cmd.ExecuteNonQuery();
                }
                // Re-write attachment rows + files.
                DeleteAttachmentRowsAndFiles(mail.MID, tx);
                SaveAttachments(mail, tx);
                tx.Commit();
            }
            MailsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DeleteMail(string mid)
    {
        lock (_gate)
        {
            using (var tx = _connection.BeginTransaction())
            {
                DeleteAttachmentRowsAndFiles(mid, tx);
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM mails WHERE mid = @mid";
                    cmd.Parameters.AddWithValue("@mid", mid);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            MailsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void AddMails(IEnumerable<WinLinkMail> mails)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var mail in mails)
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR REPLACE INTO mails
                        (mid, datetime, from_addr, to_addr, cc, subject, mbo, body, tag, location, flags, mailbox)
                        VALUES (@mid,@dt,@from,@to,@cc,@subj,@mbo,@body,@tag,@loc,@flags,@mailbox)";
                    BindMail(cmd, mail);
                    cmd.ExecuteNonQuery();
                }
                SaveAttachments(mail, tx);
            }
            tx.Commit();
            MailsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh() { /* no in-memory cache — reads hit the DB directly */ }

    public void Dispose()
    {
        try { _connection.Close(); } catch (Exception) { }
        _connection.Dispose();
    }

    // --- helpers ---

    private static WinLinkMail ReadMail(SqliteDataReader r) => new WinLinkMail
    {
        MID = r.GetString(0),
        DateTime = DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
        From = r.IsDBNull(2) ? null : r.GetString(2),
        To = r.IsDBNull(3) ? null : r.GetString(3),
        Cc = r.IsDBNull(4) ? null : r.GetString(4),
        Subject = r.IsDBNull(5) ? null : r.GetString(5),
        Mbo = r.IsDBNull(6) ? null : r.GetString(6),
        Body = r.IsDBNull(7) ? null : r.GetString(7),
        Tag = r.IsDBNull(8) ? null : r.GetString(8),
        Location = r.IsDBNull(9) ? null : r.GetString(9),
        Flags = r.GetInt32(10),
        Mailbox = r.IsDBNull(11) ? "Inbox" : r.GetString(11),
    };

    private static void BindMail(SqliteCommand cmd, WinLinkMail m)
    {
        cmd.Parameters.AddWithValue("@mid", m.MID);
        cmd.Parameters.AddWithValue("@dt", m.DateTime.ToString("o"));
        cmd.Parameters.AddWithValue("@from", (object?)m.From ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@to", (object?)m.To ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cc", (object?)m.Cc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@subj", (object?)m.Subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mbo", (object?)m.Mbo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@body", (object?)m.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tag", (object?)m.Tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@loc", (object?)m.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@flags", m.Flags);
        cmd.Parameters.AddWithValue("@mailbox", (object?)m.Mailbox ?? "Inbox");
    }

    private static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private void SaveAttachments(WinLinkMail mail, SqliteTransaction? tx = null)
    {
        if (mail.Attachments == null) return;
        foreach (var att in mail.Attachments)
        {
            string fileName = $"{SafeName(mail.MID)}__{SafeName(att.Name ?? "att")}";
            if (att.Data != null) File.WriteAllBytes(Path.Combine(_attachmentsPath, fileName), att.Data);
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO attachments (mail_mid, filename, filepath) VALUES (@mid,@fn,@fp)";
            cmd.Parameters.AddWithValue("@mid", mail.MID);
            cmd.Parameters.AddWithValue("@fn", att.Name ?? "att");
            cmd.Parameters.AddWithValue("@fp", fileName);
            cmd.ExecuteNonQuery();
        }
    }

    private List<WinLinkMailAttachement> LoadAttachments(string mid)
    {
        var list = new List<WinLinkMailAttachement>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT filename, filepath FROM attachments WHERE mail_mid = @mid";
        cmd.Parameters.AddWithValue("@mid", mid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var att = new WinLinkMailAttachement { Name = reader.GetString(0) };
            string full = Path.Combine(_attachmentsPath, reader.GetString(1));
            if (File.Exists(full)) att.Data = File.ReadAllBytes(full);
            list.Add(att);
        }
        return list;   // empty list (not null) so consumers can iterate safely
    }

    private void DeleteAttachmentRowsAndFiles(string mid, SqliteTransaction? tx = null)
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT filepath FROM attachments WHERE mail_mid = @mid";
            cmd.Parameters.AddWithValue("@mid", mid);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try { File.Delete(Path.Combine(_attachmentsPath, reader.GetString(0))); } catch (Exception) { }
            }
        }
        using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM attachments WHERE mail_mid = @mid";
            del.Parameters.AddWithValue("@mid", mid);
            del.ExecuteNonQuery();
        }
    }
}
