// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Remote;
using OpenQA.Selenium.Support.UI;
using Wasm.Performance.Driver;
using DevHostServerProgram = Microsoft.AspNetCore.Blazor.DevServer.Server.Program;

namespace Wasm.Perforrmance.Driver
{
    public class Program
    {
        // Run Selenium using a headless browser?
        static readonly bool RunHeadlessBrowser
            = !System.Diagnostics.Debugger.IsAttached;
         // = false;

        static readonly TimeSpan TestAppTimeOut = TimeSpan.FromMinutes(10);

        public static async Task Main()
        {
            using var seleniumServer = await SeleniumServer.StartAsync();
            using var browser = CreateBrowser(seleniumServer.Uri);
            using var testApp = RunTestApp();

            var address = testApp.Services.GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
                .First();

            browser.Url = address + "#automated";
            browser.Navigate();

            var results = await RunBenchmark(browser);
            Console.WriteLine(JsonSerializer.Serialize(results));
        }

        private static Task<List<BenchmarkResult>> RunBenchmark(RemoteWebDriver browser)
        {
            var remoteLogs = new RemoteLogs(browser);
            var tcs = new TaskCompletionSource<List<BenchmarkResult>>();

            Task.Run(() =>
            {
                try
                {
                    var results = new List<BenchmarkResult>();
                    var lastSeenCount = 0;
                    new WebDriverWait(browser, TimeSpan.FromSeconds(90)).Until(c =>
                    {
                        var logs = remoteLogs.GetLog("browser");
                        for (var i = lastSeenCount; i < logs.Count; i++)
                        {
                            Console.WriteLine(logs[i].Message);
                            if (logs[i].Message.Contains("Benchmark completed", StringComparison.Ordinal))
                            {
                                return true;
                            }
                        }

                        lastSeenCount = logs.Count;
                        return false;
                    });

                    var js = (string)browser.ExecuteScript("return JSON.stringify(window.benchmarksResults)");
                    tcs.TrySetResult(JsonSerializer.Deserialize<List<BenchmarkResult>>(js, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        private class BenchmarkResult
        {
            public string Name { get; set; }

            public bool Success { get; set; }

            public int NumExecutions { get; set; }

            public double Duration { get; set; }
        }

        static IHost RunTestApp()
        {
            var testAppRoot = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(f => f.Key == "TestAppLocation")
                .Value;

            var args = new[]
            {
                "--urls", "http://127.0.0.1:0",
                "--contentroot", testAppRoot,
                "--applicationpath", typeof(TestApp.Startup).Assembly.Location,
            };

            var host = DevHostServerProgram.BuildWebHost(args);
            RunInBackgroundThread(host.Start);
            return host;
        }

        static void RunInBackgroundThread(Action action)
        {
            var isDone = new ManualResetEvent(false);

            ExceptionDispatchInfo edi = null;
            Task.Run(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    edi = ExceptionDispatchInfo.Capture(ex);
                }

                isDone.Set();
            });

            if (!isDone.WaitOne(TestAppTimeOut))
            {
                throw new TimeoutException("Timed out waiting for: " + action);
            }

            if (edi != null)
            {
                throw edi.SourceException;
            }
        }

        static RemoteWebDriver CreateBrowser(Uri uri)
        {
            var options = new ChromeOptions();


            if (RunHeadlessBrowser)
            {
                options.AddArgument("--headless");
            }

            // Log errors
            options.SetLoggingPreference(LogType.Browser, LogLevel.All);

            var attempt = 0;
            const int MaxAttempts = 3;
            do
            {
                try
                {
                    // The driver opens the browser window and tries to connect to it on the constructor.
                    // Under heavy load, this can cause issues
                    // To prevent this we let the client attempt several times to connect to the server, increasing
                    // the max allowed timeout for a command on each attempt linearly.
                    var driver = new RemoteWebDriver(
                        uri,
                        options.ToCapabilities(),
                        TimeSpan.FromSeconds(60).Add(TimeSpan.FromSeconds(attempt * 60)));

                    driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(1);

                    return driver;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error initializing RemoteWebDriver: {ex.Message}");
                }

                attempt++;

            } while (attempt < MaxAttempts);

            throw new InvalidOperationException("Couldn't create a Selenium remote driver client. The server is irresponsive");
        }
    }
}
