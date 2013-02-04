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
using Microsoft.PythonTools.Navigation;
using Microsoft.PythonTools.Repl;
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
    /// Provides the command for starting the Python Debug REPL window.
    /// </summary>
    class OpenDebugReplCommand : Command {

        internal static IReplWindow/*!*/ EnsureReplWindow() {
            var compModel = PythonToolsPackage.ComponentModel;
            var provider = compModel.GetService<IReplWindowProvider>();

            string replId = PythonDebugReplEvaluatorProvider.GetDebugReplId();
            var window = provider.FindReplWindow(replId);
            if (window == null) {
                window = provider.CreateReplWindow(PythonToolsPackage.Instance.ContentType, "Python Debug Interactive", typeof(PythonLanguageInfo).GUID, replId);

                window.SetOptionValue(ReplOptions.UseSmartUpDown, PythonToolsPackage.Instance.InteractiveDebugOptionsPage.Options.ReplSmartHistory);
            }
            return window;
        }

        public override void DoCommand(object sender, EventArgs args) {
            var window = (IReplWindow)EnsureReplWindow();
            IVsWindowFrame windowFrame = (IVsWindowFrame)((ToolWindowPane)window).Frame;

            ErrorHandler.ThrowOnFailure(windowFrame.Show());
            window.Focus();
        }

        public override EventHandler BeforeQueryStatus {
            get {
                return QueryStatusMethod;
            }
        }

        private void QueryStatusMethod(object sender, EventArgs args) {
            var oleMenu = sender as OleMenuCommand;

            oleMenu.Visible = true;
            oleMenu.Enabled = true;
            oleMenu.Supported = true;
        }

        public string Description {
            get {
                return "Python Interactive Debug";
            }
        }

        public override int CommandId {
            get { return (int)PkgCmdIDList.cmdidDebugReplWindow; }
        }
    }
}
