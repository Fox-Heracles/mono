//
// test-runner.cs
//
// Author:
//   Zoltan Varga (vargaz@gmail.com)
//
// Copyright (C) 2008 Novell, Inc (http://www.novell.com)
//
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//
using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.Text;
using System.Text.RegularExpressions;

#if !MOBILE_STATIC
using Mono.Unix.Native;
#endif

//
// This is a simple test runner with support for parallel execution
//

public class TestRunner
{
	const string TEST_TIME_FORMAT = "mm\\:ss\\.fff";
	const string ENV_TIMEOUT = "TEST_DRIVER_TIMEOUT_SEC";
	const string MONO_PATH = "MONO_PATH";

	class ProcessData {
		public string test;
		public StringBuilder stdout, stderr;
		public string stdoutName, stderrName;
	}

	class TestInfo {
		public string test, opt_set;
	}

	public static int Main (String[] args) {
		// Defaults
		int concurrency = 1;
		int timeout = 2 * 60; // in seconds
		int expectedExitCode = 0;
		string testsuiteName = null;
		string inputFile = null;

		string disabled_tests = null;
		string runtime = "mono";
		string config = null;
		string mono_path = null;
		var opt_sets = new List<string> ();

		string aot_run_flags = null;
		string aot_build_flags = null;

		// Process options
		int i = 0;
		while (i < args.Length) {
			if (args [i].StartsWith ("-")) {
				if (args [i] == "-j") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to -j command line option.");
						return 1;
					}
					if (args [i + 1] == "a")
						concurrency = Environment.ProcessorCount;
					else
						concurrency = Int32.Parse (args [i + 1]);
					i += 2;
				} else if (args [i] == "--timeout") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --timeout command line option.");
						return 1;
					}
					timeout = Int32.Parse (args [i + 1]);
					i += 2;
				} else if (args [i] == "--disabled") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --disabled command line option.");
						return 1;
					}
					disabled_tests = args [i + 1];
					i += 2;
				} else if (args [i] == "--runtime") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --runtime command line option.");
						return 1;
					}
					runtime = args [i + 1];
					i += 2;
				} else if (args [i] == "--config") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --config command line option.");
						return 1;
					}
					config = args [i + 1];
					i += 2;
				} else if (args [i] == "--opt-sets") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --opt-sets command line option.");
						return 1;
					}
					foreach (var s in args [i + 1].Split ())
						opt_sets.Add (s);
					i += 2;
				} else if (args [i] == "--expected-exit-code") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --expected-exit-code command line option.");
						return 1;
					}
					expectedExitCode = Int32.Parse (args [i + 1]);
					i += 2;
				} else if (args [i] == "--testsuite-name") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --testsuite-name command line option.");
						return 1;
					}
					testsuiteName = args [i + 1];
					i += 2;
				} else if (args [i] == "--input-file") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --input-file command line option.");
						return 1;
					}
					inputFile = args [i + 1];
					i += 2;
				} else if (args [i] == "--runtime") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --runtime command line option.");
						return 1;
					}
					runtime = args [i + 1];
					i += 2;
				} else if (args [i] == "--mono-path") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --mono-path command line option.");
						return 1;
					}
					mono_path = args [i + 1].Substring(0, args [i + 1].Length);

					i += 2;
				} else if (args [i] == "--aot-run-flags") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --aot-run-flags command line option.");
						return 1;
					}
					aot_run_flags = args [i + 1].Substring(0, args [i + 1].Length);
					i += 2;
				} else if (args [i] == "--aot-build-flags") {
					if (i + 1 >= args.Length) {
						Console.WriteLine ("Missing argument to --aot-build-flags command line option.");
						return 1;
					}
					aot_build_flags = args [i + 1].Substring(0, args [i + 1].Length);
					i += 2;
				} else {
					Console.WriteLine ("Unknown command line option: '" + args [i] + "'.");
					return 1;
				}
			} else {
				break;
			}
		}

		if (String.IsNullOrEmpty (testsuiteName)) {
			Console.WriteLine ("Missing the required --testsuite-name command line option.");
			return 1;
		}

		var disabled = new Dictionary <string, string> ();

		if (disabled_tests != null) {
			foreach (string test in disabled_tests.Split ())
				disabled [test] = test;
		}

		var tests = new List<string> ();

		if (!String.IsNullOrEmpty (inputFile)) {
			tests.AddRange (File.ReadAllLines (inputFile));
		} else {
			// The remaining arguments are the tests
			for (int j = i; j < args.Length; ++j)
				if (!disabled.ContainsKey (args [j]))
					tests.Add (args [j]);
		}

		var passed = new List<ProcessData> ();
		var failed = new List<ProcessData> ();
		var timedout = new List<ProcessData> ();

		object monitor = new object ();

		Console.WriteLine ("Running tests: ");

		var test_info = new Queue<TestInfo> ();
		if (opt_sets.Count == 0) {
			foreach (string s in tests)
				test_info.Enqueue (new TestInfo { test = s });
		} else {
			foreach (string opt in opt_sets) {
				foreach (string s in tests)
					test_info.Enqueue (new TestInfo { test = s, opt_set = opt });
			}
		}

		/* compute the max length of test names, to have an optimal output width */
		int output_width = -1;
		foreach (TestInfo ti in test_info) {
			if (ti.test.Length > output_width)
				output_width = Math.Min (120, ti.test.Length);
		}

		if (aot_build_flags != null)  {
			Console.WriteLine("AOT compiling tests");

			object aot_monitor = new object ();
			var aot_queue = new Queue<String> (tests); 

			List<Thread> build_threads = new List<Thread> (concurrency);

			for (int j = 0; j < concurrency; ++j) {
				Thread thread = new Thread (() => {
					while (true) {
						String test_name;

						lock (aot_monitor) {
							if (aot_queue.Count == 0)
								break;
							test_name = aot_queue.Dequeue ();
						}

						string test_bitcode_output = test_name + "_bitcode_tmp";
						string test_bitcode_arg = ",temp-path=" + test_bitcode_output;
						string aot_args = aot_build_flags + test_bitcode_arg + " " + test_name;

						Directory.CreateDirectory(test_bitcode_output);

						ProcessStartInfo job = new ProcessStartInfo (runtime, aot_args);
						job.UseShellExecute = false;
						job.EnvironmentVariables[ENV_TIMEOUT] = timeout.ToString();
						job.EnvironmentVariables[MONO_PATH] = mono_path;
						Process compiler = new Process ();
						compiler.StartInfo = job;

						compiler.Start ();

						if (!compiler.WaitForExit (timeout * 1000)) {
							try {
								compiler.Kill ();
							} catch {
							}
							throw new Exception(String.Format("Timeout AOT compiling tests, output in {0}", test_bitcode_output));
						} else if (compiler.ExitCode != 0) {
							throw new Exception(String.Format("Error AOT compiling tests, output in {0}", test_bitcode_output));
						} else {
							Directory.Delete (test_bitcode_output, true);
						}
					}
				});

				thread.Start ();

				build_threads.Add (thread);
			}

			for (int j = 0; j < build_threads.Count; ++j)
				build_threads [j].Join ();

			Console.WriteLine("Done compiling");
		}

		List<Thread> threads = new List<Thread> (concurrency);

		DateTime test_start_time = DateTime.UtcNow;

		for (int j = 0; j < concurrency; ++j) {
			Thread thread = new Thread (() => {
				while (true) {
					TestInfo ti;

					lock (monitor) {
						if (test_info.Count == 0)
							break;
						ti = test_info.Dequeue ();
					}

					var output = new StringWriter ();

					string test = ti.test;
					string opt_set = ti.opt_set;

					output.Write (String.Format ("{{0,-{0}}} ", output_width), test);

					string test_invoke;

					if (aot_run_flags != null)
						test_invoke = aot_run_flags + " " + test;
					else
						test_invoke = test;

					/* Spawn a new process */
					string process_args;
					if (opt_set == null)
						process_args = test_invoke;
					else
						process_args = "-O=" + opt_set + " " + test_invoke;

					ProcessStartInfo info = new ProcessStartInfo (runtime, process_args);
					info.UseShellExecute = false;
					info.RedirectStandardOutput = true;
					info.RedirectStandardError = true;
					info.EnvironmentVariables[ENV_TIMEOUT] = timeout.ToString();
					if (config != null)
						info.EnvironmentVariables["MONO_CONFIG"] = config;
					if (mono_path != null)
						info.EnvironmentVariables[MONO_PATH] = mono_path;
					Process p = new Process ();
					p.StartInfo = info;

					ProcessData data = new ProcessData ();
					data.test = test;

					string log_prefix = "";
					if (opt_set != null)
						log_prefix = "." + opt_set.Replace ("-", "no").Replace (",", "_");

					data.stdoutName = test + log_prefix + ".stdout";
					data.stdout = new StringBuilder ();

					data.stderrName = test + log_prefix + ".stderr";
					data.stderr = new StringBuilder ();

					p.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e) {
						if (e.Data != null) {
							data.stdout.AppendLine (e.Data);
						}
					};

					p.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e) {
						if (e.Data != null) {
							data.stderr.AppendLine (e.Data);
						}
					};

					var start = DateTime.UtcNow;

					p.Start ();

					p.BeginOutputReadLine ();
					p.BeginErrorReadLine ();

					if (!p.WaitForExit (timeout * 1000)) {
						lock (monitor) {
							timedout.Add (data);
						}

#if !MOBILE_STATIC
						// Force the process to print a thread dump
						try {
							Syscall.kill (p.Id, Signum.SIGQUIT);
							Thread.Sleep (1000);
						} catch {
						}
#endif

						output.Write ($"timed out ({timeout}s)");

						try {
							p.Kill ();
						} catch {
						}
					} else if (p.ExitCode != expectedExitCode) {
						var end = DateTime.UtcNow;

						lock (monitor) {
							failed.Add (data);
						}

						output.Write ("failed, time: {0}, exit code: {1}", (end - start).ToString (TEST_TIME_FORMAT), p.ExitCode);
					} else {
						var end = DateTime.UtcNow;

						lock (monitor) {
							passed.Add (data);
						}

						output.Write ("passed, time: {0}", (end - start).ToString (TEST_TIME_FORMAT));
					}

					p.Close ();

					lock (monitor) {
						Console.WriteLine (output.ToString ());
					}
				}
			});

			thread.Start ();

			threads.Add (thread);
		}

		for (int j = 0; j < threads.Count; ++j)
			threads [j].Join ();

		TimeSpan test_time = DateTime.UtcNow - test_start_time;

		int npassed = passed.Count;
		int nfailed = failed.Count;
		int ntimedout = timedout.Count;

		XmlWriterSettings xmlWriterSettings = new XmlWriterSettings ();
		xmlWriterSettings.NewLineOnAttributes = true;
		xmlWriterSettings.Indent = true;
		using (XmlWriter writer = XmlWriter.Create (String.Format ("TestResult-{0}.xml", testsuiteName), xmlWriterSettings)) {
			// <?xml version="1.0" encoding="utf-8" standalone="no"?>
			writer.WriteStartDocument ();
			// <!--This file represents the results of running a test suite-->
			writer.WriteComment ("This file represents the results of running a test suite");
			// <test-results name="/home/charlie/Dev/NUnit/nunit-2.5/work/src/bin/Debug/tests/mock-assembly.dll" total="21" errors="1" failures="1" not-run="7" inconclusive="1" ignored="4" skipped="0" invalid="3" date="2010-10-18" time="13:23:35">
			writer.WriteStartElement ("test-results");
			writer.WriteAttributeString ("name", String.Format ("{0}-tests.dummy", testsuiteName));
			writer.WriteAttributeString ("total", (npassed + nfailed + ntimedout).ToString());
			writer.WriteAttributeString ("failures", (nfailed + ntimedout).ToString());
			writer.WriteAttributeString ("not-run", "0");
			writer.WriteAttributeString ("date", DateTime.Now.ToString ("yyyy-MM-dd"));
			writer.WriteAttributeString ("time", DateTime.Now.ToString ("HH:mm:ss"));
			//   <environment nunit-version="2.4.8.0" clr-version="4.0.30319.17020" os-version="Unix 3.13.0.45" platform="Unix" cwd="/home/directhex/Projects/mono/mcs/class/corlib" machine-name="marceline" user="directhex" user-domain="marceline" />
			writer.WriteStartElement ("environment");
			writer.WriteAttributeString ("nunit-version", "2.4.8.0" );
			writer.WriteAttributeString ("clr-version", Environment.Version.ToString() );
			writer.WriteAttributeString ("os-version", Environment.OSVersion.ToString() );
			writer.WriteAttributeString ("platform", Environment.OSVersion.Platform.ToString() );
			writer.WriteAttributeString ("cwd", Environment.CurrentDirectory );
			writer.WriteAttributeString ("machine-name", Environment.MachineName );
			writer.WriteAttributeString ("user", Environment.UserName );
			writer.WriteAttributeString ("user-domain", Environment.UserDomainName );
			writer.WriteEndElement ();
			//   <culture-info current-culture="en-GB" current-uiculture="en-GB" />
			writer.WriteStartElement ("culture-info");
			writer.WriteAttributeString ("current-culture", CultureInfo.CurrentCulture.Name );
			writer.WriteAttributeString ("current-uiculture", CultureInfo.CurrentUICulture.Name );
			writer.WriteEndElement ();
			//   <test-suite name="corlib_test_net_4_5.dll" success="True" time="114.318" asserts="0">
			writer.WriteStartElement ("test-suite");
			writer.WriteAttributeString ("name", String.Format ("{0}-tests.dummy", testsuiteName));
			writer.WriteAttributeString ("success", (nfailed + ntimedout == 0).ToString());
			writer.WriteAttributeString ("time", test_time.Seconds.ToString());
			writer.WriteAttributeString ("asserts", (nfailed + ntimedout).ToString());
			//     <results>
			writer.WriteStartElement ("results");
			//       <test-suite name="MonoTests" success="True" time="114.318" asserts="0">
			writer.WriteStartElement ("test-suite");
			writer.WriteAttributeString ("name","MonoTests");
			writer.WriteAttributeString ("success", (nfailed + ntimedout == 0).ToString());
			writer.WriteAttributeString ("time", test_time.Seconds.ToString());
			writer.WriteAttributeString ("asserts", (nfailed + ntimedout).ToString());
			//         <results>
			writer.WriteStartElement ("results");
			//           <test-suite name="MonoTests" success="True" time="114.318" asserts="0">
			writer.WriteStartElement ("test-suite");
			writer.WriteAttributeString ("name", testsuiteName);
			writer.WriteAttributeString ("success", (nfailed + ntimedout == 0).ToString());
			writer.WriteAttributeString ("time", test_time.Seconds.ToString());
			writer.WriteAttributeString ("asserts", (nfailed + ntimedout).ToString());
			//             <results>
			writer.WriteStartElement ("results");
			// Dump all passing tests first
			foreach (ProcessData pd in passed) {
				// <test-case name="MonoTests.Microsoft.Win32.RegistryKeyTest.bug79051" executed="True" success="True" time="0.063" asserts="0" />
				writer.WriteStartElement ("test-case");
				writer.WriteAttributeString ("name", String.Format ("MonoTests.{0}.{1}", testsuiteName, pd.test));
				writer.WriteAttributeString ("executed", "True");
				writer.WriteAttributeString ("success", "True");
				writer.WriteAttributeString ("time", "0");
				writer.WriteAttributeString ("asserts", "0");
				writer.WriteEndElement ();
			}
			// Now dump all failing tests
			foreach (ProcessData pd in failed) {
				// <test-case name="MonoTests.Microsoft.Win32.RegistryKeyTest.bug79051" executed="True" success="True" time="0.063" asserts="0" />
				writer.WriteStartElement ("test-case");
				writer.WriteAttributeString ("name", String.Format ("MonoTests.{0}.{1}", testsuiteName, pd.test));
				writer.WriteAttributeString ("executed", "True");
				writer.WriteAttributeString ("success", "False");
				writer.WriteAttributeString ("time", "0");
				writer.WriteAttributeString ("asserts", "1");
				writer.WriteStartElement ("failure");
				writer.WriteStartElement ("message");
				writer.WriteCData (FilterInvalidXmlChars (pd.stdout.ToString ()));
				writer.WriteEndElement ();
				writer.WriteStartElement ("stack-trace");
				writer.WriteCData (FilterInvalidXmlChars (pd.stderr.ToString ()));
				writer.WriteEndElement ();
				writer.WriteEndElement ();
				writer.WriteEndElement ();
			}
			// Then dump all timing out tests
			foreach (ProcessData pd in timedout) {
				// <test-case name="MonoTests.Microsoft.Win32.RegistryKeyTest.bug79051" executed="True" success="True" time="0.063" asserts="0" />
				writer.WriteStartElement ("test-case");
				writer.WriteAttributeString ("name", String.Format ("MonoTests.{0}.{1}_timedout", testsuiteName, pd.test));
				writer.WriteAttributeString ("executed", "True");
				writer.WriteAttributeString ("success", "False");
				writer.WriteAttributeString ("time", "0");
				writer.WriteAttributeString ("asserts", "1");
				writer.WriteStartElement ("failure");
				writer.WriteStartElement ("message");
				writer.WriteCData (FilterInvalidXmlChars (pd.stdout.ToString ()));
				writer.WriteEndElement ();
				writer.WriteStartElement ("stack-trace");
				writer.WriteCData (FilterInvalidXmlChars (pd.stderr.ToString ()));
				writer.WriteEndElement ();
				writer.WriteEndElement ();
				writer.WriteEndElement ();
			}
			//             </results>
			writer.WriteEndElement ();
			//           </test-suite>
			writer.WriteEndElement ();
			//         </results>
			writer.WriteEndElement ();
			//       </test-suite>
			writer.WriteEndElement ();
			//     </results>
			writer.WriteEndElement ();
			//   </test-suite>
			writer.WriteEndElement ();
			// </test-results>
			writer.WriteEndElement ();
			writer.WriteEndDocument ();
		}

		Console.WriteLine ();
		Console.WriteLine ("Time: {0}", test_time.ToString (TEST_TIME_FORMAT));
		Console.WriteLine ();
		Console.WriteLine ("{0,4} test(s) passed", npassed);
		Console.WriteLine ("{0,4} test(s) failed", nfailed);
		Console.WriteLine ("{0,4} test(s) timed out", ntimedout);

		if (nfailed > 0) {
			Console.WriteLine ();
			Console.WriteLine ("Failed test(s):");
			foreach (ProcessData pd in failed) {
				Console.WriteLine ();
				Console.WriteLine (pd.test);
				DumpFile (pd.stdoutName, pd.stdout.ToString ());
				DumpFile (pd.stderrName, pd.stderr.ToString ());
			}
		}

		if (ntimedout > 0) {
			Console.WriteLine ();
			Console.WriteLine ("Timed out test(s):");
			foreach (ProcessData pd in timedout) {
				Console.WriteLine ();
				Console.WriteLine (pd.test);
				DumpFile (pd.stdoutName, pd.stdout.ToString ());
				DumpFile (pd.stderrName, pd.stderr.ToString ());
			}
		}

		return (ntimedout == 0 && nfailed == 0) ? 0 : 1;
	}
	
	static void DumpFile (string filename, string text) {
		Console.WriteLine ("=============== {0} ===============", filename);
		Console.WriteLine (text);
		Console.WriteLine ("=============== EOF ===============");
	}

	static string FilterInvalidXmlChars (string text) {
		// Spec at http://www.w3.org/TR/2008/REC-xml-20081126/#charsets says only the following chars are valid in XML:
		// Char ::= #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]	/* any Unicode character, excluding the surrogate blocks, FFFE, and FFFF. */
		return Regex.Replace (text, @"[^\x09\x0A\x0D\x20-\uD7FF\uE000-\uFFFD\u10000-\u10FFFF]", "");
	}
}
