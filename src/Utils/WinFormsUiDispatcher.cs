/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using System.Windows.Forms;
using HTCommander.Core.Abstractions;

namespace HTCommander
{
    /// <summary>
    /// WinForms implementation of <see cref="IUiDispatcher"/>, wrapping a Control so
    /// that DataBroker (now in HTCommander.Core) can marshal callbacks onto the UI
    /// thread without depending on System.Windows.Forms.
    /// </summary>
    public sealed class WinFormsUiDispatcher : IUiDispatcher
    {
        private readonly Control _control;

        public WinFormsUiDispatcher(Control control)
        {
            _control = control ?? throw new ArgumentNullException(nameof(control));
        }

        public bool IsDispatchRequired => _control.InvokeRequired;

        public void Post(Action action) => _control.BeginInvoke(action);

        public void Invoke(Action action) => _control.Invoke(action);
    }
}
