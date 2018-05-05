﻿using Backtrace.Base;
using Backtrace.Extensions;
using Backtrace.Interfaces;
using Backtrace.Model;
using Backtrace.Model.Database;
using Backtrace.Services;
using Backtrace.Tests.DatabaseTests.Model;
using Backtrace.Types;
using Moq;
using NUnit.Framework;
using RichardSzalay.MockHttp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Backtrace.Tests.IntegrationTests
{
    [TestFixture(Author = "Konrad Dysput", Category = "Integrationtests.DatabaseFlow", Description = "Test application flow for database flush methods")]
    public class DatabaseIntegrationTests
    {
        /// <summary>
        /// Client
        /// </summary>
        BacktraceClient _backtraceClient;

        /// <summary>
        /// Last database entry
        /// </summary>
        BacktraceDatabaseEntry<object> _lastEntry;

        /// <summary>
        /// Enable hard drive write errors
        /// </summary>
        bool _enableWriteErrors = false;

        bool _writeFail = false;

        /// <summary>
        /// Get new database entry 
        /// </summary>
        /// <returns>Database entry mock</returns>
        protected BacktraceDatabaseEntry<object> GetEntry()
        {
            //mock single entry
            var mockEntry = new Mock<BacktraceDatabaseEntry<object>>();
            mockEntry.Setup(n => n.Delete());
            mockEntry.SetupProperty(n => n.Entry, null);

            //mock entry writer
            mockEntry.Object.EntryWriter = new MockBacktraceDatabaseWriter();
            if (_enableWriteErrors)
            {
                ((MockBacktraceDatabaseWriter)mockEntry.Object.EntryWriter).writeFail = _writeFail;
            }

            return mockEntry.Object;
        }

        [SetUp]
        public void Setup()
        {
            _lastEntry = GetEntry();
            //get project path
            string projectPath = Environment.CurrentDirectory;
            //setup credentials
            var credentials = new BacktraceCredentials("https://validurl.com/", "validToken");
            //mock api
            var serverUrl = $"{credentials.BacktraceHostUri.AbsoluteUri}post?format=json&token={credentials.Token}";
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(serverUrl)
                .Respond("application/json", "{'object' : 'aaa'}");
            var api = new BacktraceApi<object>(credentials, 0)
            {
                HttpClient = mockHttp.ToHttpClient()
            };

            //mock file context
            var mockFileContext = new Mock<IBacktraceDatabaseFileContext<object>>();
            mockFileContext.Setup(n => n.GetEntries())
                .Returns(new List<FileInfo>());
            mockFileContext.Setup(n => n.RemoveOrphaned(It.IsAny<IEnumerable<BacktraceDatabaseEntry<object>>>()));

            //mock cache
            var mockCacheContext = new Mock<IBacktraceDatabaseContext<object>>();

            mockCacheContext.Setup(n => n.Add(It.IsAny<BacktraceData<object>>()))
                .Callback(() =>
                {
                    mockCacheContext.Object.Add(_lastEntry);
                    _lastEntry = GetEntry();
                })
                .Returns(_lastEntry);


            var database = new BacktraceDatabase<object>(new BacktraceDatabaseSettings(projectPath) { RetryBehavior = RetryBehavior.NoRetry })
            {
                BacktraceDatabaseContext = mockCacheContext.Object,
                BacktraceDatabaseFileContext = mockFileContext.Object,
            };

            //setup new client
            _backtraceClient = new BacktraceClient(backtraceCredentials: credentials, database: database, reportPerMin: 0)
            {
                BacktraceApi = api
            };
        }

        private void DivideByZeroMethod()
        {
            int x = 0;
            var result = 5 / x;
        }

        private void OutOfRangeMethod()
        {
            int[] x = new int[1];
            x[1] = 1 - 1;
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(10)]
        [Test(Author = "Konrad Dysput", Description = "Test application flow with available database and without client rate limit and without any write  errors")]
        public async Task TestApplicationFlowWithoutClientRateLimitingAndWithoutWriteFails(int totalReports)
        {
            //set client rate limit to unlimited
            _backtraceClient.BacktraceApi.SetClientRateLimit(0);
            //ignore generating minidump files
            _backtraceClient.MiniDumpType = MiniDumpType.None;

            for (int i = 0; i < totalReports; i++)
            {
                try
                {
                    DivideByZeroMethod();
                }
                catch (DivideByZeroException e)
                {
                    var report = e.ToBacktraceReport();
                    var result = await _backtraceClient.SendAsync(report);
                    Assert.AreEqual(result.Status, BacktraceResultStatus.Ok);
                }

                try
                {
                    OutOfRangeMethod();
                }
                catch (IndexOutOfRangeException e)
                {
                    var report = e.ToBacktraceReport();
                    var result = await _backtraceClient.SendAsync(report);
                    Assert.AreEqual(result.Status, BacktraceResultStatus.Ok);
                }
            }
        }


        [TestCase(2, 1)]
        [TestCase(5, 2)]
        [TestCase(15, 5)]
        [Test(Author = "Konrad Dysput", Description = "Test application flow with available database and with client rate limit and without any write errors")]
        public async Task TestApplicationFlowWithClientRateLimitingAndWithoutWriteFails(int totalReports, int limit)
        {
            //set client rate limit to unlimited
            _backtraceClient.BacktraceApi.SetClientRateLimit(Convert.ToUInt32(limit));
            //total ignored reports removed by ReportWatcher
            int totalIgnoredReports = 0;
            _backtraceClient.OnClientReportLimitReached = (BacktraceReport report) =>
            {
                totalIgnoredReports++;
            };
            int totalAttemps = 0;
            _backtraceClient.BeforeSend = (BacktraceData<object> data) =>
            {
                totalAttemps++;
                return data;
            };
            //ignore generating minidump files
            _backtraceClient.MiniDumpType = MiniDumpType.None;

            for (int i = 0; i < totalReports; i++)
            {
                try
                {
                    DivideByZeroMethod();
                }
                catch (DivideByZeroException e)
                {
                    var report = e.ToBacktraceReport();
                    var result = await _backtraceClient.SendAsync(report);
                }
                try
                {
                    OutOfRangeMethod();
                }
                catch (IndexOutOfRangeException e)
                {
                    var report = e.ToBacktraceReport();
                    var result = await _backtraceClient.SendAsync(report);
                }
            }
            //totalReports * 2 is because we send two exceptions
            Assert.AreEqual(totalIgnoredReports, totalReports * 2 - limit);
        }

        [TestCase(2, 1, 0)]
        [TestCase(2, 1, 50)]
        [TestCase(2, 1, 100)]
        [TestCase(5, 2, 0)]
        [TestCase(5, 2, 50)]
        [TestCase(5, 2, 100)]
        [TestCase(15, 5, 0)]
        [TestCase(15, 5, 50)]
        [TestCase(15, 5, 100)]
        [Test(Author = "Konrad Dysput", Description = "Test application flow with available database and with client rate limit and with write errors")]
        public async Task TestApplicationFlowWithClientRateLimitingAndWithWriteFails(int totalReports, int limit, int successRate)
        {
            Random random = new Random();
            _enableWriteErrors = true;
            //set client rate limit to unlimited
            _backtraceClient.BacktraceApi.SetClientRateLimit(Convert.ToUInt32(limit));
            //total ignored reports removed by ReportWatcher
            int totalIgnoredReports = 0;
            _backtraceClient.OnClientReportLimitReached = (BacktraceReport report) =>
            {
                totalIgnoredReports++;
            };
            int totalAttemps = 0;
            _backtraceClient.BeforeSend = (BacktraceData<object> data) =>
            {
                totalAttemps++;
                return data;
            };
            //ignore generating minidump files
            _backtraceClient.MiniDumpType = MiniDumpType.None;

            for (int i = 0; i < totalReports; i++)
            {
                var percentage = random.Next(0, 100);
                _writeFail = percentage < successRate;
                try
                {
                    DivideByZeroMethod();
                }
                catch (DivideByZeroException e)
                {
                    var report = e.ToBacktraceReport();
                    var result = await _backtraceClient.SendAsync(report);
                }
                try
                {
                    OutOfRangeMethod();
                }
                catch (IndexOutOfRangeException e)
                {
                    var report = e.ToBacktraceReport();
                    var result = await _backtraceClient.SendAsync(report);
                }
            }
            //totalReports * 2 is because we send two exceptions
            Assert.AreEqual(totalIgnoredReports, totalReports * 2 - limit);
        }

    }
}