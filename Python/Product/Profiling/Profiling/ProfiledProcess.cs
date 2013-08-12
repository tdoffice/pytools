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
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace Microsoft.PythonTools.Profiling {
    class ProfiledProcess {
        private readonly string _exe, _args, _dir;
        private readonly ProcessorArchitecture _arch;
        private readonly Process _process;

        public ProfiledProcess(string exe, string args, string dir, Dictionary<string, string> envVars, ProcessorArchitecture arch) {
            if (arch != ProcessorArchitecture.X86 && arch != ProcessorArchitecture.Amd64) {
                throw new InvalidOperationException(String.Format("Unsupported architecture: {0}", arch));
            }
            if (dir.EndsWith("\\")) {
                dir = dir.Substring(0, dir.Length - 1);
            }
            if (String.IsNullOrEmpty(dir)) {
                dir = ".";
            }
            _exe = exe;
            _args = args;
            _dir = dir;
            _arch = arch;

            ProcessStartInfo processInfo;
            string pythonInstallDir = GetPythonToolsInstallPath();
            string dll = _arch == ProcessorArchitecture.Amd64 ? "VsPyProf.dll" : "VsPyProfX86.dll";
            string arguments = "\"" + Path.Combine(pythonInstallDir, "proflaun.py") + "\" " +
                "\"" + Path.Combine(pythonInstallDir, dll) + "\" " +
                "\"" + dir + "\" " +
                _args;

            processInfo = new ProcessStartInfo(_exe, arguments);
            if (PythonToolsPackage.Instance.DebuggingOptionsPage.WaitOnNormalExit) {
                processInfo.EnvironmentVariables["VSPYPROF_WAIT_ON_NORMAL_EXIT"] = "1";
            }
            if (PythonToolsPackage.Instance.DebuggingOptionsPage.WaitOnAbnormalExit) {
                processInfo.EnvironmentVariables["VSPYPROF_WAIT_ON_ABNORMAL_EXIT"] = "1";
            }
            
            processInfo.CreateNoWindow = false;
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardOutput = false;

            if (envVars != null) {
                foreach (var keyValue in envVars) {
                    processInfo.EnvironmentVariables[keyValue.Key] = keyValue.Value;
                }
            }

            _process = new Process();
            _process.StartInfo = processInfo;
        }

        public void StartProfiling(string filename) {
            StartPerfMon(filename);
            
            _process.EnableRaisingEvents = true;
            _process.Exited += (sender, args) => {
                try {
                    // Exited event is fired on a random thread pool thread, we need to handle exceptions.
                    StopPerfMon();
                } catch (InvalidOperationException e) {
                    MessageBox.Show(String.Format("Unable to stop performance monitor: {0}", e.Message), "Python Tools for Visual Studio");
                }
                var procExited = ProcessExited;
                if (procExited != null) {
                    procExited(this, EventArgs.Empty);
                }
            };

            _process.Start();
        }

        public event EventHandler ProcessExited;

        private void StartPerfMon(string filename) {
            string perfToolsPath = GetPerfToolsPath();

            string perfMonPath = Path.Combine(perfToolsPath, "VSPerfMon.exe");

            var psi = new ProcessStartInfo(perfMonPath, "/trace /output:" + filename);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            var process = Process.Start(psi);

            string perfCmdPath = Path.Combine(perfToolsPath, "VSPerfCmd.exe");

            psi = new ProcessStartInfo(perfCmdPath, "/waitstart");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            process = Process.Start(psi);
            process.WaitForExit();
            if (process.ExitCode != 0) {
                throw new InvalidOperationException("Starting perf cmd failed: " + process.StandardOutput.ReadToEnd());
            }
        }

        private void StopPerfMon() {
            string perfToolsPath = GetPerfToolsPath();

            string perfMonPath = Path.Combine(perfToolsPath, "VSPerfCmd.exe");

            var psi = new ProcessStartInfo(perfMonPath, "/shutdown");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            var process = Process.Start(psi);
            process.WaitForExit();
            if (process.ExitCode != 0) {
                throw new InvalidOperationException("Shutting down perf cmd failed: " + process.StandardOutput.ReadToEnd() + "\r\n" + process.StandardError.ReadToEnd());
            }
        }

        private string GetPerfToolsPath() {
            RegistryKey key = Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\VisualStudio\\" + AssemblyVersionInfo.VSVersion);
            var shFolder = key.GetValue("ShellFolder") as string;
            if (shFolder == null) {
                throw new InvalidOperationException("Cannot find shell folder for Visual Studio");
            }

            string perfToolsPath;
            if (_arch == ProcessorArchitecture.Amd64) {
                perfToolsPath = @"Team Tools\Performance Tools\x64";
            } else {
                perfToolsPath = @"Team Tools\Performance Tools\";
            }
            perfToolsPath = Path.Combine(shFolder, perfToolsPath);
            return perfToolsPath;
        }


        internal void StopProfiling() {
            _process.Kill();
        }

        // This is duplicated throughout different assemblies in PythonTools, so search for it if you update it.
        private static string GetPythonToolsInstallPath() {
            string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (File.Exists(Path.Combine(path, "vspyprof.dll"))) {
                return path;
            }

            // running from the GAC in remote attach scenario.  Look to the VS install dir.
            using (var configKey = OpenVisualStudioKey()) {
                var installDir = configKey.GetValue("InstallDir") as string;
                if (installDir != null) {
                    var toolsPath = Path.Combine(installDir, "Extensions\\Microsoft\\PythonProfiling\\2.0");
                    if (File.Exists(Path.Combine(toolsPath, "vspyprof.dll"))) {
                        return toolsPath;
                    }
                }
            }

            Debug.Assert(false, "Unable to determine Python Tools installation path");
            return string.Empty;
        }

        private static Win32.RegistryKey OpenVisualStudioKey() {
            if (PythonProfilingPackage.Instance != null) {
                return PythonProfilingPackage.Instance.ApplicationRegistryRoot;
            }

            if (Environment.Is64BitOperatingSystem) {
#if DEV12
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey("Software\\Microsoft\\VisualStudio\\12.0");
#elif DEV11
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey("Software\\Microsoft\\VisualStudio\\11.0");
#elif DEV10
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey("Software\\Microsoft\\VisualStudio\\10.0");
#else
#error Unsupported version of Visual Studio
#endif
            } else {
#if DEV12
                return Microsoft.Win32.Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\VisualStudio\\12.0");
#elif DEV11
                return Microsoft.Win32.Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\VisualStudio\\11.0");
#elif DEV10
                return Microsoft.Win32.Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\VisualStudio\\10.0");
#else
#error Unsupported version of Visual Studio
#endif
            }
        }

    }
}