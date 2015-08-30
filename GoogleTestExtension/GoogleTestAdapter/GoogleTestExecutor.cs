﻿using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace GoogleTestAdapter
{
    [ExtensionUri(ExecutorUriString)]
    public class GoogleTestExecutor : AbstractGoogleTestAdapterClass, ITestExecutor
    {
        public const string ExecutorUriString = Constants.identifierUri;
        public static readonly Uri ExecutorUri = new Uri(ExecutorUriString);

        private bool Canceled = false;

        public GoogleTestExecutor() : this(null) { }

        public GoogleTestExecutor(IOptions options) : base(options) {}

        public void Cancel()
        {
            DebugUtils.CheckDebugModeForExecutionCode();

            Canceled = true;
        }

        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            DebugUtils.CheckDebugModeForExecutionCode(frameworkHandle);

            Canceled = false;
            foreach (string executable in sources)
            {
                if (Canceled)
                {
                    break;
                }

                GoogleTestDiscoverer Discoverer = new GoogleTestDiscoverer(Options);
                List<TestCase> AllCases = Discoverer.GetTestsFromExecutable(frameworkHandle, executable);
                RunTests(true, AllCases, AllCases, runContext, frameworkHandle);
            }
        }

        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            DebugUtils.CheckDebugModeForExecutionCode(frameworkHandle);

            Canceled = false;
            List<TestCase> AllTestCasesInAllExecutables = new List<TestCase>();
            HashSet<string> Executables = new HashSet<string>();
            var TestCases = tests as TestCase[] ?? tests.ToArray();
            foreach (TestCase TestCase in TestCases)
            {
                Executables.Add(TestCase.Source);
            }

            foreach (string Executable in Executables)
            {
                AllTestCasesInAllExecutables.AddRange(new GoogleTestDiscoverer(Options).GetTestsFromExecutable(frameworkHandle, Executable));
            }
            RunTests(false, AllTestCasesInAllExecutables, TestCases, runContext, frameworkHandle);
        }

        private void RunTests(bool runAll, IEnumerable<TestCase> allCases, IEnumerable<TestCase> cases, IRunContext runContext, IFrameworkHandle handle)
        {
            Dictionary<string, List<TestCase>> groupedCases = new Dictionary<string, List<TestCase>>();
            foreach (TestCase testcase in cases)
            {
                List<TestCase> group;
                if (groupedCases.ContainsKey(testcase.Source))
                {
                    group = groupedCases[testcase.Source];
                }
                else
                {
                    group = new List<TestCase>();
                    groupedCases.Add(testcase.Source, group);
                }
                group.Add(testcase);
            }

            TestCase[] AllCasesAsArray = allCases.ToArray();
            foreach (string executable in groupedCases.Keys)
            {
                if (Canceled)
                {
                    break;
                }
                try
                {
                    RunTestsFromExecutable(runAll, executable, AllCasesAsArray, groupedCases[executable], runContext, handle);
                }
                catch (Exception e)
                {
                    handle.SendMessage(TestMessageLevel.Error, e.Message);
                    handle.SendMessage(TestMessageLevel.Error, e.StackTrace);
                }
            }

        }

        // ReSharper disable once UnusedParameter.Local
        private void RunTestsFromExecutable(bool runAll, string executable, IEnumerable<TestCase> allCases, IEnumerable<TestCase> cases, IRunContext runContext, IFrameworkHandle handle)
        {
            var testCases = cases as TestCase[] ?? cases.ToArray();
            foreach (TestCase testcase in testCases)
            {
                handle.RecordStart(testcase);
            }

            string ResultXmlFile = Path.GetTempFileName();
            string WorkingDir = Path.GetDirectoryName(executable);
            foreach(string Arguments in new GoogleTestCommandLine(runAll, executable.Length, allCases, testCases, ResultXmlFile, handle, Options).GetCommandLines())
            {
                List<string> ConsoleOutput = ProcessUtils.GetOutputOfCommand(handle, WorkingDir, executable, Arguments, Options.PrintTestOutput, false);
                foreach (TestResult testResult in CollectTestResults(ResultXmlFile, ConsoleOutput, testCases, handle))
                {
                    handle.RecordResult(testResult);
                }
            }
        }

        private List<TestResult> CollectTestResults(string resultXmlFile, List<string> consoleOutput, IEnumerable<TestCase> cases, IFrameworkHandle handle)
        {
            List<TestResult> TestResults = new List<TestResult>();

            var TestCases = cases as TestCase[] ?? cases.ToArray();
            GoogleTestResultXmlParser XmlParser = new GoogleTestResultXmlParser(resultXmlFile, TestCases, handle);
            TestResults.AddRange(XmlParser.GetTestResults());

            if (TestResults.Count < TestCases.Length)
            {
                GoogleTestResultStandardOutputParser ConsoleParser = new GoogleTestResultStandardOutputParser(consoleOutput, TestCases, handle);
                List<TestResult> ConsoleResults = ConsoleParser.GetTestResults();
                foreach (TestResult testResult in ConsoleResults.Where(tr => !TestResults.Exists(tr2 => tr.TestCase.FullyQualifiedName == tr2.TestCase.FullyQualifiedName)))
                {
                    TestResults.Add(testResult);
                }

                if (TestResults.Count < TestCases.Length)
                {
                    foreach (TestCase testcase in TestCases.Where(c => !TestResults.Exists(tr => tr.TestCase.FullyQualifiedName == c.FullyQualifiedName)))
                    {
                        string ErrorMsg = ConsoleParser.CrashedTestCase == null ? ""
                            : "probably crash of test " + ConsoleParser.CrashedTestCase.DisplayName;
                        TestResults.Add(new TestResult(testcase)
                        {
                            ComputerName = Environment.MachineName,
                            Outcome = TestOutcome.NotFound,
                            ErrorMessage = ErrorMsg
                        });
                    }
                }
            }

            return TestResults;
        }

    }

}