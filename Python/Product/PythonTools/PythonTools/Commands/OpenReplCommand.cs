﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using Microsoft.PythonTools.Interpreter;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Repl;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.PythonTools.Commands {
#if INTERACTIVE_WINDOW
    using IReplWindow = IInteractiveWindow;
    using IReplEvaluator = IInteractiveEngine;
#endif

    /// <summary>
    /// Provides the command for starting the Python REPL window.
    /// </summary>
    class OpenReplCommand : Command {
        private readonly int _cmdId;
        private readonly IPythonInterpreterFactory _factory;

        public OpenReplCommand(int cmdId, IPythonInterpreterFactory factory) {
            _cmdId = cmdId;
            _factory = factory;
        }

        public override void DoCommand(object sender, EventArgs args) {
            // These commands are project-insensitive, so pass null for project.
            var window = (ToolWindowPane)ExecuteInReplCommand.EnsureReplWindow(_factory, null);

            IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
            ErrorHandler.ThrowOnFailure(windowFrame.Show());
            ((IReplWindow)window).Focus();
        }

        public override EventHandler BeforeQueryStatus {
            get {
                return QueryStatusMethod;
            }
        }

        private void QueryStatusMethod(object sender, EventArgs args) {
            var oleMenu = sender as OleMenuCommand;

            if (_factory == null) {
                oleMenu.Visible = false;
                oleMenu.Enabled = false;
                oleMenu.Supported = false;
            } else {
                oleMenu.Visible = true;
                oleMenu.Enabled = true;
                oleMenu.Supported = true;
                oleMenu.Text = Description;
            }
        }

        public string Description {
            get {
                return _factory.Description + " Interactive";
            }
        }
        
        public override int CommandId {
            get { return (int)_cmdId; }
        }
    }
}
