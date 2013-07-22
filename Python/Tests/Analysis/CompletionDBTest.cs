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

extern alias analysis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.PythonTools;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestUtilities;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using ProcessOutput = analysis::Microsoft.PythonTools.ProcessOutput;

namespace PythonToolsTests {
    [TestClass]
    public class CompletionDBTest {
        [ClassInitialize]
        public static void DoDeployment(TestContext context) {
            AssertListener.Initialize();
            TestData.Deploy();
        }

        [TestMethod, Priority(0)]
        public void TestOpen() {
            var untested = new List<string>();

            foreach (var path in PythonPaths.Versions) {
                if (!File.Exists(path.Path)) {
                    untested.Add(path.Version.ToString());
                    continue;
                }
                Console.WriteLine(path.Path);

                Guid testId = Guid.NewGuid();
                var testDir = Path.Combine(Path.GetTempPath(), testId.ToString());
                Directory.CreateDirectory(testDir);

                // run the scraper
                var startInfo = new ProcessStartInfo(path.Path,
                    String.Format("\"{2}\" \"{0}\" \"{1}\"",
                        testDir,
                        TestData.GetPath("CompletionDB"),
                        TestData.GetPath("PythonScraper.py")
                    )
                );
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.UseShellExecute = false;

                var process = Process.Start(startInfo);
                var receiver = new OutputReceiver();
                process.OutputDataReceived += receiver.OutputDataReceived;
                process.ErrorDataReceived += receiver.OutputDataReceived;
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                
                process.WaitForExit();

                // it should succeed
                Assert.AreEqual(0, process.ExitCode, "Bad exit code: " + process.ExitCode + "\r\n" + receiver.Output.ToString());

                // perform some basic validation
                dynamic builtinDb = Unpickle.Load(new FileStream(Path.Combine(testDir, path.Version.Is3x() ? "builtins.idb" : "__builtin__.idb"), FileMode.Open, FileAccess.Read));
                if (path.Version.Is2x()) { // no open in 3.x
                    foreach (var overload in builtinDb["members"]["open"]["value"]["overloads"]) {
                        Assert.AreEqual("__builtin__", overload["ret_type"][0]["module_name"]);
                        Assert.AreEqual("file", overload["ret_type"][0]["type_name"]);
                    }

                    if (!path.Path.Contains("Iron")) {
                        // http://pytools.codeplex.com/workitem/799
                        var arr = (IList<object>)builtinDb["members"]["list"]["value"]["members"]["__init__"]["value"]["overloads"];
                        Assert.AreEqual(
                            "args",
                            ((dynamic)(arr[0]))["args"][1]["name"]
                        );
                    }
                }

                if (!path.Path.Contains("Iron")) {
                    dynamic itertoolsDb = Unpickle.Load(new FileStream(Path.Combine(testDir, "itertools.idb"), FileMode.Open, FileAccess.Read));
                    var tee = itertoolsDb["members"]["tee"]["value"];
                    var overloads = tee["overloads"];
                    var nArg = overloads[0]["args"][1];
                    Assert.AreEqual("n", nArg["name"]);
                    Assert.AreEqual("2", nArg["default_value"]);

                    dynamic sreDb = Unpickle.Load(new FileStream(Path.Combine(testDir, "_sre.idb"), FileMode.Open, FileAccess.Read));
                    var members = sreDb["members"];
                    Assert.IsTrue(members.ContainsKey("SRE_Pattern"));
                    Assert.IsTrue(members.ContainsKey("SRE_Match"));
                }
            }

            if (untested.Count > 0) {
                Assert.Inconclusive("Did not test with version(s) " + string.Join(", ", untested));
            }
        }

        [TestMethod, Priority(0)]
        public void TestPthFiles() {
            var outputPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(outputPath);

            // run the analyzer
            using (var output = ProcessOutput.RunHiddenAndCapture("Microsoft.PythonTools.Analyzer.exe",
                "/lib", TestData.GetPath(@"TestData\PathStdLib"),
                "/version", "2.7",
                "/outdir", outputPath,
                "/indir", TestData.GetPath("CompletionDB"))) {
                output.Wait();
                Console.WriteLine("* Stdout *");
                foreach (var line in output.StandardOutputLines) {
                    Console.WriteLine(line);
                }
                Console.WriteLine("* Stderr *");
                foreach (var line in output.StandardErrorLines) {
                    Console.WriteLine(line);
                }
                Assert.AreEqual(0, output.ExitCode);
            }

            File.Copy(TestData.GetPath(@"CompletionDB\__builtin__.idb"), Path.Combine(outputPath, "__builtin__.idb"));

            var fact = InterpreterFactoryCreator.CreateAnalysisInterpreterFactory(new Version(2, 7));
            var paths = new List<string> { outputPath };
            paths.AddRange(Directory.EnumerateDirectories(outputPath));
            var typeDb = new PythonTypeDatabase(fact, paths);
            var module = typeDb.GetModule("SomeLib");
            Assert.AreNotEqual(null, module);
            var fooMod = module.GetMember(null, "foo");
            Assert.AreNotEqual(null, fooMod);

            var cClass = ((IPythonModule)fooMod).GetMember(null, "C");
            Assert.AreNotEqual(null, cClass);

            Assert.AreEqual(PythonMemberType.Class, cClass.MemberType);
        }

        /// <summary>
        /// Checks that members removed or introduced in later versions show up or don't in
        /// earlier versions as appropriate.
        /// </summary>
        [TestMethod, Priority(0)]
        public void VersionedSharedDatabase() {
            var twoFive = PythonTypeDatabase.CreateDefaultTypeDatabase(new Version(2, 5));
            var twoSix = PythonTypeDatabase.CreateDefaultTypeDatabase(new Version(2, 6));
            var twoSeven = PythonTypeDatabase.CreateDefaultTypeDatabase(new Version(2, 7));
            var threeOh = PythonTypeDatabase.CreateDefaultTypeDatabase(new Version(3, 0));
            var threeOne = PythonTypeDatabase.CreateDefaultTypeDatabase(new Version(3, 1));
            var threeTwo = PythonTypeDatabase.CreateDefaultTypeDatabase(new Version(3, 2));

            // new in 2.6
            Assert.AreEqual(null, twoFive.BuiltinModule.GetAnyMember("bytearray"));
            foreach (var version in new[] { twoSix, twoSeven, threeOh, threeOne, threeTwo }) {
                Assert.AreNotEqual(version, version.BuiltinModule.GetAnyMember("bytearray"));
            }

            // new in 2.7
            Assert.AreEqual(null, twoSix.BuiltinModule.GetAnyMember("memoryview"));
            foreach (var version in new[] { twoSeven, threeOh, threeOne, threeTwo }) {
                Assert.AreNotEqual(version, version.BuiltinModule.GetAnyMember("memoryview"));
            }

            // not in 3.0
            foreach (var version in new[] { twoFive, twoSix, twoSeven }) {
                Assert.AreNotEqual(null, version.BuiltinModule.GetAnyMember("StandardError"));
            }

            foreach (var version in new[] { threeOh, threeOne, threeTwo }) {
                Assert.AreEqual(null, version.BuiltinModule.GetAnyMember("StandardError"));
            }

            // new in 3.0
            foreach (var version in new[] { twoFive, twoSix, twoSeven }) {
                Assert.AreEqual(null, version.BuiltinModule.GetAnyMember("exec"));
                Assert.AreEqual(null, version.BuiltinModule.GetAnyMember("print"));
            }

            foreach (var version in new[] { threeOh, threeOne, threeTwo }) {
                Assert.AreNotEqual(null, version.BuiltinModule.GetAnyMember("exec"));
                Assert.AreNotEqual(null, version.BuiltinModule.GetAnyMember("print"));
            }


            // new in 3.1
            foreach (var version in new[] { twoFive, twoSix, twoSeven, threeOh }) {
                Assert.AreEqual(null, version.GetModule("sys").GetMember(null, "int_info"));
            }

            foreach (var version in new[] { threeOne, threeTwo }) {
                Assert.AreNotEqual(null, version.GetModule("sys").GetMember(null, "int_info"));
            }

            // new in 3.2
            foreach (var version in new[] { twoFive, twoSix, twoSeven, threeOh, threeOne }) {
                Assert.AreEqual(null, version.GetModule("sys").GetMember(null, "setswitchinterval"));
            }

            foreach (var version in new[] { threeTwo }) {
                Assert.AreNotEqual(null, version.GetModule("sys").GetMember(null, "setswitchinterval"));
            }
        }
    }
}
