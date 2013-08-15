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
using System.Threading.Tasks;
using Microsoft.PythonTools.Interpreter;

namespace Microsoft.PythonTools.Project {
    static class EasyInstall {
        private static readonly KeyValuePair<string, string>[] UnbufferedEnv = new[] { 
            new KeyValuePair<string, string>("PYTHONUNBUFFERED", "1")
        };

        // The relative path from PrefixPath, and true if it is a Python script
        // that needs to be run with the interpreter.
        private static readonly KeyValuePair<string, bool>[] EasyInstallLocations = new[] {
            new KeyValuePair<string, bool>(Path.Combine("Scripts", "easy_install-script.py"), true),
            new KeyValuePair<string, bool>("easy_install-script.py", true),
            new KeyValuePair<string, bool>(Path.Combine("Scripts", "easy_install.exe"), false),
            new KeyValuePair<string, bool>("easy_install.exe", false)
        };

        private static string GetEasyInstallPath(IPythonInterpreterFactory factory, out bool isScript) {
            foreach (var path in EasyInstallLocations) {
                string easyInstallPath = Path.Combine(factory.Configuration.PrefixPath, path.Key);
                isScript = path.Value;
                if (File.Exists(easyInstallPath)) {
                    return easyInstallPath;
                }
            }
            isScript = false;
            return null;
        }

        private static Task<int> ContinueRun(
            Task task,
            IPythonInterpreterFactory factory,
            Redirector output,
            bool elevate,
            params string[] cmd
        ) {
            return task.ContinueWith((Func<Task, int>)(t => {
                bool isScript;
                var easyInstallPath = GetEasyInstallPath(factory, out isScript);
                if (easyInstallPath == null) {
                    throw new FileNotFoundException("Cannot find setuptools ('easy_install.exe')");
                }

                var args = cmd.ToList();
                args.Insert(0, "--always-copy");
                args.Insert(0, "--always-unzip");
                if (isScript) {
                    args.Insert(0, ProcessOutput.QuoteSingleArgument(easyInstallPath));
                    easyInstallPath = factory.Configuration.InterpreterPath;
                }
                using (var proc = ProcessOutput.Run(
                    easyInstallPath,
                    args,
                    factory.Configuration.PrefixPath,
                    UnbufferedEnv,
                    false,
                    output,
                    false,
                    elevate
                )) {
                    proc.Wait();
                    return proc.ExitCode ?? -1;
                }
            }), TaskContinuationOptions.LongRunning);
        }

        public static Task Install(
            IPythonInterpreterFactory factory,
            string package,
            bool elevate,
            Redirector output = null
        ) {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetResult(null);
            return ContinueRun(tcs.Task, factory, output, elevate, package);
        }

        public static Task<bool> Install(
            IPythonInterpreterFactory factory,
            string package,
            IServiceProvider site,
            bool elevate,
            Redirector output = null
        ) {
            Task task;
            bool isScript;
            if (site != null && GetEasyInstallPath(factory, out isScript) == null) {
                task = Pip.QueryInstallPip(factory, site, SR.GetString(SR.InstallEasyInstall), elevate, output);
            } else {
                var tcs = new TaskCompletionSource<object>();
                tcs.SetResult(null);
                task = tcs.Task;
            }

            var task2 = task.ContinueWith(t => {
                if (output != null) {
                    output.WriteLine(SR.GetString(SR.PackageInstalling, package));
                    if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
            });

            return ContinueRun(task2, factory, output, elevate, package).ContinueWith(t => {
                var exitCode = t.Result;

                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(SR.GetString(SR.PackageInstallSucceeded, package));
                    } else {
                        output.WriteLine(SR.GetString(SR.PackageInstallFailedExitCode, package, exitCode));
                    }
                    if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
                return exitCode == 0;
            });
        }
    }
}
