// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
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
using DevHostServerProgram = Microsoft.AspNetCore.Blazor.DevServer.Server.Program;

namespace Wasm.Perforrmance.Driver
{
    public class Program
    {
        // Total time we'll give for the test app before we consider it timed out.
        const int Port = 9001;

        // Run Selenium using a headless browser?
        static readonly bool RunHeadlessBrowser
            = !Debugger.IsAttached;
        // = false;

        static readonly TimeSpan TestAppTimeOut = TimeSpan.FromMinutes(10);

        public static async Task Main()
        {
            using var process = StartSeleniumServer();
            var seleniumServerUri = await EnsureSeleniumInitialized();
            using var browser = CreateBrowser(seleniumServerUri);
            using var testApp = RunTestApp();

            var address = testApp.Services.GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
                .First();

            browser.Url = address;
            browser.Navigate();
        }

        static IHost RunTestApp()
        {
            var testAppRoot = Path.Combine(Directory.GetCurrentDirectory(), "..", "TestApp");

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

        private static Process StartSeleniumServer()
        {
            var outputLock = new object();

            var psi = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = $"run selenium-standalone start -- -- -port {Port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.FileName = "cmd";
                psi.Arguments = $"/c npm {psi.Arguments}";
            }

            var process = Process.Start(psi);
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                lock (outputLock)
                {
                    Console.WriteLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                lock (outputLock)
                {
                    Console.Error.WriteLine(e.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        static async Task<Uri> EnsureSeleniumInitialized()
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(1),
            };

            var uri = new UriBuilder("http", "localhost", Port, "/wd/hub").Uri;

            const int MaxRetries = 30;
            var retries = 0;

            while (true)
            {
                retries++;
                await Task.Delay(1000);
                try
                {
                    var response = await httpClient.GetAsync(uri);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return uri;
                    }
                }
                catch when (retries < MaxRetries)
                {
                }
            }
        }

        static IWebDriver CreateBrowser(Uri uri)
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
