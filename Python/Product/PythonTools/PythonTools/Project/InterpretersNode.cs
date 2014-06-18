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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Build.Evaluation;
using Microsoft.PythonTools.Commands;
using Microsoft.PythonTools.Interpreter;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Project;
using Clipboard = System.Windows.Forms.Clipboard;
using MessageBox = System.Windows.Forms.MessageBox;
using Task = System.Threading.Tasks.Task;
using VsCommands = Microsoft.VisualStudio.VSConstants.VSStd97CmdID;
using VsCommands2K = Microsoft.VisualStudio.VSConstants.VSStd2KCmdID;
using VsMenus = Microsoft.VisualStudioTools.Project.VsMenus;

namespace Microsoft.PythonTools.Project {
    /// <summary>
    /// Represents an interpreter as a node in the Solution Explorer.
    /// </summary>
    [ComVisible(true)]
    internal class InterpretersNode : HierarchyNode {
        private readonly MSBuildProjectInterpreterFactoryProvider _interpreters;
        internal readonly IPythonInterpreterFactory _factory;
        private readonly bool _isReference;
        private readonly bool _canDelete;
        private readonly FileSystemWatcher _fileWatcher;
        private readonly Timer _timer;
        private bool _checkedItems, _checkingItems, _disposed;
        private readonly SemaphoreSlim _installingPackage;
        private int _waitingToInstallPackage;

        public InterpretersNode(
            PythonProjectNode project,
            ProjectItem item,
            IPythonInterpreterFactory factory,
            bool isInterpreterReference,
            bool canDelete
        )
            : base(project, ChooseElement(project, item)) {
            ExcludeNodeFromScc = true;

            _interpreters = project.Interpreters;
            _factory = factory;
            _isReference = isInterpreterReference;
            _canDelete = canDelete;
            _installingPackage = new SemaphoreSlim(1);

            if (Directory.Exists(_factory.Configuration.LibraryPath)) {
                // TODO: Need to handle watching for creation
                try {
                    _fileWatcher = new FileSystemWatcher(_factory.Configuration.LibraryPath);
                } catch (ArgumentException) {
                    // Path was not actually valid, despite Directory.Exists
                    // returning true.
                }
                if (_fileWatcher != null) {
                    try {
                        _fileWatcher.IncludeSubdirectories = true;
                        _fileWatcher.Deleted += PackagesChanged;
                        _fileWatcher.Created += PackagesChanged;
                        _fileWatcher.EnableRaisingEvents = true;
                        // Only create the timer if the file watcher is running.
                        _timer = new Timer(CheckPackages);
                    } catch (IOException) {
                        // Raced with directory deletion
                        _fileWatcher.Dispose();
                        _fileWatcher = null;
                    }
                }
            }
        }

        public override int MenuCommandId {
            get { return PythonConstants.EnvironmentMenuId; }
        }

        public override Guid MenuGroupId {
            get { return GuidList.guidPythonToolsCmdSet; }
        }

        private static ProjectElement ChooseElement(PythonProjectNode project, ProjectItem item) {
            if (item != null) {
                return new MsBuildProjectElement(project, item);
            } else {
                return new VirtualProjectElement(project);
            }
        }

        public override void Close() {
            _installingPackage.Wait();

            if (!_disposed && _fileWatcher != null) {
                _fileWatcher.Dispose();
                _timer.Dispose();
            }
            _disposed = true;

            base.Close();
        }

        private void PackagesChanged(object sender, FileSystemEventArgs e) {
            // have a delay before refreshing because there's probably more than one write,
            // so we wait until things have been quiet for a second.
            _timer.Change(1000, Timeout.Infinite);
        }

        private void CheckPackages(object arg) {
            UIThread.InvokeTask(() => CheckPackagesAsync())
                .HandleAllExceptions(SR.ProductName, GetType())
                .DoNotWait();
        }

        private async Task CheckPackagesAsync() {
            bool prevChecked = _checkedItems;
            // Use _checkingItems to prevent the expanded state from
            // disappearing too quickly.
            _checkingItems = true;
            _checkedItems = true;
            if (!Directory.Exists(_factory.Configuration.LibraryPath)) {
                _checkingItems = false;
                ProjectMgr.OnPropertyChanged(this, (int)__VSHPROPID.VSHPROPID_Expandable, 0);
                return;
            }

            HashSet<string> lines;
            bool anyChanges = false;
            try {
                lines = await Pip.List(_factory);
            } catch (NoInterpretersException) {
                return;
            }
            if (ProjectMgr == null || ProjectMgr.IsClosed) {
                return;
            }

            var existing = AllChildren.ToDictionary(c => c.Url);

            // remove the nodes which were uninstalled.
            foreach (var keyValue in existing) {
                if (!lines.Contains(keyValue.Key)) {
                    RemoveChild(keyValue.Value);
                    anyChanges = true;
                }
            }

            // remove already existing nodes so we don't add them a 2nd time
            lines.ExceptWith(existing.Keys);

            // add the new nodes
            foreach (var line in lines) {
                AddChild(new InterpretersPackageNode(ProjectMgr, line));
                anyChanges = true;
            }
            _checkingItems = false;

            ProjectMgr.OnInvalidateItems(this);
            if (!prevChecked) {
                if (anyChanges) {
                    ProjectMgr.OnPropertyChanged(this, (int)__VSHPROPID.VSHPROPID_Expandable, 0);
                }
                ExpandItem(EXPANDFLAGS.EXPF_CollapseFolder);
            }

            if (prevChecked && anyChanges) {
                var withDb = _factory as IPythonInterpreterFactoryWithDatabase;
                if (withDb != null) {
                    withDb.GenerateDatabase(GenerateDatabaseOptions.SkipUnchanged);
                }
            }
        }

        public override Guid ItemTypeGuid {
            get { return PythonConstants.InterpreterItemTypeGuid; }
        }

        internal override int ExecCommandOnNode(Guid cmdGroup, uint cmd, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) {
            if (cmdGroup == VsMenus.guidStandardCommandSet2K) {
                switch((VsCommands2K)cmd) {
                    case CommonConstants.OpenFolderInExplorerCmdId:
                        Process.Start(new ProcessStartInfo {
                            FileName = _factory.Configuration.PrefixPath,
                            Verb = "open",
                            UseShellExecute = true
                        });
                        return VSConstants.S_OK;
                }
            }
            
            if (cmdGroup == GuidList.guidPythonToolsCmdSet) {
                switch (cmd) {
                    case PythonConstants.ActivateEnvironment:
                        return ProjectMgr.SetInterpreterFactory(_factory);
                    case PythonConstants.InstallPythonPackage:
                        InterpretersPackageNode.InstallNewPackage(this).HandleAllExceptions(SR.ProductName).DoNotWait();
                        return VSConstants.S_OK;
                    case PythonConstants.InstallRequirementsTxt:
                        InterpretersPackageNode.InstallNewPackage(
                            this,
                            "-r " + ProcessOutput.QuoteSingleArgument(
                                CommonUtils.GetAbsoluteFilePath(ProjectMgr.ProjectHome, "requirements.txt")
                            ),
                            true,
                            PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ElevatePip
                        ).HandleAllExceptions(SR.ProductName).DoNotWait();
                        return VSConstants.S_OK;
                    case PythonConstants.GenerateRequirementsTxt:
                        GenerateRequirementsTxt().HandleAllExceptions(SR.ProductName).DoNotWait();
                        return VSConstants.S_OK;
                    case PythonConstants.OpenInteractiveForEnvironment:
                        try {
                            var window = ExecuteInReplCommand.EnsureReplWindow(_factory, ProjectMgr);
                            var pane = window as ToolWindowPane;
                            if (pane != null) {
                                ErrorHandler.ThrowOnFailure(((IVsWindowFrame)pane.Frame).Show());
                                window.Focus();
                            }
                        } catch (InvalidOperationException ex) {
                            MessageBox.Show(SR.GetString(SR.ErrorOpeningInteractiveWindow, ex), SR.ProductName);
                        }
                        return VSConstants.S_OK;
                }
            } 
            
            if (cmdGroup == ProjectMgr.SharedCommandGuid) {
                switch ((SharedCommands)cmd) {
                    case SharedCommands.OpenCommandPromptHere:
                        var pyProj = ProjectMgr as PythonProjectNode;
                        if (pyProj != null && _factory != null && _factory.Configuration != null) {
                            return pyProj.OpenCommandPrompt(
                                _factory.Configuration.PrefixPath,
                                _factory,
                                _factory.Description
                            );
                        }
                        break;
                    case SharedCommands.CopyFullPath:
                        Clipboard.SetText(_factory.Configuration.InterpreterPath);
                        return VSConstants.S_OK;
                }
            }
            
            return base.ExecCommandOnNode(cmdGroup, cmd, nCmdexecopt, pvaIn, pvaOut);
        }

        private async Task GenerateRequirementsTxt() {
            var projectHome = ProjectMgr.ProjectHome;
            var txt = CommonUtils.GetAbsoluteFilePath(projectHome, "requirements.txt");

            string[] existing = null;
            bool addNew = false;
            if (File.Exists(txt)) {
                try {
                    existing = TaskDialog.CallWithRetry(
                        _ => File.ReadAllLines(txt),
                        ProjectMgr.Site,
                        SR.ProductName,
                        SR.GetString(SR.RequirementsTxtFailedToRead),
                        SR.GetString(SR.ErrorDetail),
                        SR.GetString(SR.Retry),
                        SR.GetString(SR.Cancel)
                    );
                } catch (OperationCanceledException) {
                    return;
                }

                var td = new TaskDialog(ProjectMgr.Site) {
                    Title = SR.ProductName,
                    MainInstruction = SR.GetString(SR.RequirementsTxtExists),
                    Content = SR.GetString(SR.RequirementsTxtExistsQuestion),
                    AllowCancellation = true,
                    CollapsedControlText = SR.GetString(SR.RequirementsTxtContentCollapsed),
                    ExpandedControlText = SR.GetString(SR.RequirementsTxtContentExpanded),
                    ExpandedInformation = string.Join(Environment.NewLine, existing)
                };
                var replace = new TaskDialogButton(
                    SR.GetString(SR.RequirementsTxtReplace),
                    SR.GetString(SR.RequirementsTxtReplaceHelp)
                );
                var refresh = new TaskDialogButton(
                    SR.GetString(SR.RequirementsTxtRefresh),
                    SR.GetString(SR.RequirementsTxtRefreshHelp)
                );
                var update = new TaskDialogButton(
                    SR.GetString(SR.RequirementsTxtUpdate),
                    SR.GetString(SR.RequirementsTxtUpdateHelp)
                );
                td.Buttons.Add(replace);
                td.Buttons.Add(refresh);
                td.Buttons.Add(update);
                td.Buttons.Add(TaskDialogButton.Cancel);
                var selection = td.ShowModal();
                if (selection == TaskDialogButton.Cancel) {
                    return;
                } else if (selection == replace) {
                    existing = null;
                } else if (selection == update) {
                    addNew = true;
                }
            }
            
            var items = await Pip.Freeze(_factory);

            if (File.Exists(txt) && !ProjectMgr.QueryEditFiles(false, txt)) {
                return;
            }

            try {
                TaskDialog.CallWithRetry(
                    _ => {
                        if (items.Any()) {
                            File.WriteAllLines(txt, MergeRequirements(existing, items, addNew));
                        } else if (existing == null) {
                            File.WriteAllText(txt, "");
                        }
                    },
                    ProjectMgr.Site, 
                    SR.ProductName,
                    SR.GetString(SR.RequirementsTxtFailedToWrite),
                    SR.GetString(SR.ErrorDetail),
                    SR.GetString(SR.Retry),
                    SR.GetString(SR.Cancel)
                );
            } catch (OperationCanceledException) {
                return;
            }

            var existingNode = ProjectMgr.FindNodeByFullPath(txt);
            if (existingNode == null || existingNode.IsNonMemberItem) {
                if (!ProjectMgr.QueryEditProjectFile(false)) {
                    return;
                }
                try {
                    existingNode = TaskDialog.CallWithRetry(
                        _ => {
                            ErrorHandler.ThrowOnFailure(ProjectMgr.AddItem(
                                ProjectMgr.ID,
                                VSADDITEMOPERATION.VSADDITEMOP_LINKTOFILE,
                                Path.GetFileName(txt),
                                1,
                                new[] { txt },
                                IntPtr.Zero,
                                new VSADDRESULT[1]
                            ));

                            return ProjectMgr.FindNodeByFullPath(txt);
                        },
                        ProjectMgr.Site,
                        SR.ProductName,
                        SR.GetString(SR.RequirementsTxtFailedToAddToProject),
                        SR.GetString(SR.ErrorDetail),
                        SR.GetString(SR.Retry),
                        SR.GetString(SR.Cancel)
                    );
                } catch (OperationCanceledException) {
                }
            }
        }

        internal const string FindRequirementRegex = @"
            (?<!\#.*)       # ensure we are not in a comment
            (?<spec>        # <spec> includes name, version and whitespace
                (?<name>[^\s\#<>=!]+)           # just the name, no whitespace
                (\s*(?<cmp><=|>=|<|>|!=|==)\s*
                    (?<ver>[^\s\#]+)
                )?          # cmp and ver are optional
            )";

        internal static IEnumerable<string> MergeRequirements(
            IEnumerable<string> original,
            IEnumerable<string> updates,
            bool addNew
        ) {
            if (original == null) {
                foreach (var req in updates.OrderBy(r => r)) {
                    yield return req;
                }
                yield break;
            }

            var findRequirement = new Regex(FindRequirementRegex, RegexOptions.IgnorePatternWhitespace);
            var existing = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var m in updates.SelectMany(req => findRequirement.Matches(req).Cast<Match>())) {
                existing[m.Groups["name"].Value] = m.Value;
            }

            var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var _line in original) {
                var line = _line;
                foreach(var m in findRequirement.Matches(line).Cast<Match>()) {
                    string newReq;
                    var name = m.Groups["name"].Value;
                    if (existing.TryGetValue(name, out newReq)) {
                        line = findRequirement.Replace(line, m2 =>
                            name.Equals(m2.Groups["name"].Value, StringComparison.InvariantCultureIgnoreCase) ?
                                newReq :
                                m2.Value
                        );
                        seen.Add(name);
                    }
                }
                yield return line;
            }

            if (addNew) {
                foreach (var req in existing
                    .Where(kv => !seen.Contains(kv.Key))
                    .Select(kv => kv.Value)
                    .OrderBy(v => v)
                ) {
                    yield return req;
                }
            }
        }

        internal async Task BeginPackageChange() {
            Interlocked.Increment(ref _waitingToInstallPackage);
            await _installingPackage.WaitAsync();

            if (!_disposed && _fileWatcher != null) {
                _fileWatcher.EnableRaisingEvents = false;
            }
        }

        internal void PackageChangeDone() {
            if (Interlocked.Decrement(ref _waitingToInstallPackage) == 0) {
                if (!_disposed && _fileWatcher != null) {
                    _fileWatcher.EnableRaisingEvents = true;
                    ThreadPool.QueueUserWorkItem(CheckPackages);
                }
            }
            _installingPackage.Release();
        }

        public new PythonProjectNode ProjectMgr {
            get {
                return (PythonProjectNode)base.ProjectMgr;
            }
        }

        internal override bool CanDeleteItem(__VSDELETEITEMOPERATION deleteOperation) {
            if (_waitingToInstallPackage != 0) {
                return false;
            }

            if (deleteOperation == __VSDELETEITEMOPERATION.DELITEMOP_RemoveFromProject) {
                // Interpreter and InterpreterReference can both be removed from
                // the project.
                return true;
            } else if (deleteOperation == __VSDELETEITEMOPERATION.DELITEMOP_DeleteFromStorage) {
                // Only Interpreter can be deleted.
                return _canDelete;
            }
            return false;
        }

        public override void Remove(bool removeFromStorage) {
            // If _canDelete, a prompt has already been shown by VS.
            Remove(removeFromStorage, !_canDelete);
        }

        private void Remove(bool removeFromStorage, bool showPrompt) {
            if (_waitingToInstallPackage != 0) {
                // Prevent the environment from being deleting while installing.
                // This situation should not occur through the UI, but might be
                // invocable through DTE.
                return;
            }

            if (showPrompt && !Utilities.IsInAutomationFunction(ProjectMgr.Site)) {
                string message = SR.GetString(removeFromStorage ?
                        SR.EnvironmentDeleteConfirmation :
                        SR.EnvironmentRemoveConfirmation,
                    Caption,
                    _factory.Configuration.PrefixPath);
                int res = VsShellUtilities.ShowMessageBox(
                    ProjectMgr.Site,
                    string.Empty,
                    message,
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                if (res != 1) {
                    return;
                }
            }

            //Make sure we can edit the project file
            if (!ProjectMgr.QueryEditProjectFile(false)) {
                throw Marshal.GetExceptionForHR(VSConstants.OLE_E_PROMPTSAVECANCELLED);
            }

            ProjectMgr.RemoveInterpreter(_factory, !_isReference && removeFromStorage && _canDelete);
        }

        /// <summary>
        /// Show interpreter display (description and version).
        /// </summary>
        public override string Caption {
            get {
                return _factory.Description;
            }
        }

        public new MsBuildProjectElement ItemNode {
            get {
                return (MsBuildProjectElement)base.ItemNode;
            }
        }

        /// <summary>
        /// Prevent editing the description
        /// </summary>
        public override string GetEditLabel() {
            return null;
        }

        public override object GetIconHandle(bool open) {
            if (ProjectMgr == null) {
                return null;
            }

            int index;
            if (!_interpreters.IsAvailable(_factory)) {
                index = ProjectMgr.GetIconIndex(PythonProjectImageName.MissingInterpreter);
            } else if (_interpreters.ActiveInterpreter == _factory) {
                index = ProjectMgr.GetIconIndex(PythonProjectImageName.ActiveInterpreter);
            } else {
                index = ProjectMgr.GetIconIndex(PythonProjectImageName.Interpreter);
            }
            return this.ProjectMgr.ImageHandler.GetIconHandle(index);
        }

        protected override VSOVERLAYICON OverlayIconIndex {
            get {
                if (!Directory.Exists(Url)) {
                    return (VSOVERLAYICON)__VSOVERLAYICON2.OVERLAYICON_NOTONDISK;
                } else if (_isReference) {
                    return VSOVERLAYICON.OVERLAYICON_SHORTCUT;
                }
                return base.OverlayIconIndex;
            }
        }

        /// <summary>
        /// Interpreter node cannot be dragged.
        /// </summary>
        protected internal override string PrepareSelectedNodesForClipBoard() {
            return null;
        }

        
        
        /// <summary>
        /// Disable Copy/Cut/Paste commands on interpreter node.
        /// </summary>
        internal override int QueryStatusOnNode(Guid cmdGroup, uint cmd, IntPtr pCmdText, ref QueryStatusResult result) {
            if (cmdGroup == VsMenus.guidStandardCommandSet2K) {
                switch ((VsCommands2K)cmd) {
                    case CommonConstants.OpenFolderInExplorerCmdId:
                        result = QueryStatusResult.SUPPORTED;
                        if (_factory != null && Directory.Exists(_factory.Configuration.PrefixPath)) {
                            result |= QueryStatusResult.ENABLED;
                        }
                        return VSConstants.S_OK;
                }
            }
            
            if (cmdGroup == GuidList.guidPythonToolsCmdSet) {
                switch (cmd) {
                    case PythonConstants.ActivateEnvironment:
                        result |= QueryStatusResult.SUPPORTED;
                        if (_interpreters.IsAvailable(_factory) &&
                            _interpreters.ActiveInterpreter != _factory &&
                            Directory.Exists(_factory.Configuration.PrefixPath)
                        ) {
                            result |= QueryStatusResult.ENABLED;
                        }
                        return VSConstants.S_OK;
                    case PythonConstants.InstallPythonPackage:
                        result |= QueryStatusResult.SUPPORTED;
                        if (_interpreters.IsAvailable(_factory) &&
                            Directory.Exists(_factory.Configuration.PrefixPath)
                        ) {
                            result |= QueryStatusResult.ENABLED;
                        }
                        return VSConstants.S_OK;
                    case PythonConstants.InstallRequirementsTxt:
                        result |= QueryStatusResult.SUPPORTED;
                        if (File.Exists(CommonUtils.GetAbsoluteFilePath(ProjectMgr.ProjectHome, "requirements.txt"))) {
                            result |= QueryStatusResult.ENABLED;
                        }
                        return VSConstants.S_OK;
                    case PythonConstants.GenerateRequirementsTxt:
                        result |= QueryStatusResult.SUPPORTED;
                        if (_interpreters.IsAvailable(_factory)) {
                            result |= QueryStatusResult.ENABLED;
                        }
                        return VSConstants.S_OK;
                    case PythonConstants.OpenInteractiveForEnvironment:
                        result |= QueryStatusResult.SUPPORTED;
                        if (_interpreters.IsAvailable(_factory) &&
                            File.Exists(_factory.Configuration.InterpreterPath)
                        ) {
                            result |= QueryStatusResult.ENABLED;
                        }
                        return VSConstants.S_OK;
                }
            }
            
            if (cmdGroup == ProjectMgr.SharedCommandGuid) {
                switch ((SharedCommands)cmd) {
                    case SharedCommands.OpenCommandPromptHere:
                    case SharedCommands.CopyFullPath:
                        result |= QueryStatusResult.SUPPORTED;
                        if (_interpreters.IsAvailable(_factory) &&
                            Directory.Exists(_factory.Configuration.PrefixPath) &&
                            File.Exists(_factory.Configuration.InterpreterPath)) {
                            result |= QueryStatusResult.ENABLED;
                        }
                        return VSConstants.S_OK;
                }
            }

            return base.QueryStatusOnNode(cmdGroup, cmd, pCmdText, ref result);
        }

        public override string Url {
            get {
                if (!CommonUtils.IsValidPath(_factory.Configuration.PrefixPath)) {
                    return string.Format("UnknownInterpreter\\{0}\\{1}", _factory.Id, _factory.Configuration.Version);
                }

                return _factory.Configuration.PrefixPath;
            }
        }

        /// <summary>
        /// Defines whether this node is valid node for painting the interpreter
        /// icon.
        /// </summary>
        protected override bool CanShowDefaultIcon() {
            return true;
        }

        public override bool CanAddFiles {
            get {
                return false;
            }
        }

        protected override NodeProperties CreatePropertiesObject() {
            if (_factory is DerivedInterpreterFactory) {
                return new InterpretersNodeWithBaseInterpreterProperties(this);
            } else {
                return new InterpretersNodeProperties(this);
            }
        }

        public override object GetProperty(int propId) {
            if (propId == (int)__VSHPROPID.VSHPROPID_Expandable) {
                if (!_checkedItems) {
                    // We haven't checked if we have files on disk yet, report
                    // that we can expand until we do.
                    // We do this lazily so we don't need to spawn a process for
                    // each interpreter on project load.
                    ThreadPool.QueueUserWorkItem(CheckPackages);
                    return true;
                } else if (_checkingItems) {
                    // Still checking, so keep reporting true.
                    return true;
                }
            }

            return base.GetProperty(propId);
        }
    }
}
