// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Wasm.Performance.Driver
{
    class SeleniumServer : IDisposable
    {
        private SeleniumServer(Process process, Uri uri)
        {
            SeleniumProcess = process;
            Uri = uri;
        }

        public Uri Uri { get; }
        private Process SeleniumProcess { get; }

        public static async ValueTask<SeleniumServer> StartAsync(int port = 9001)
        {
            var outputLock = new object();

            var psi = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = $"run selenium-standalone start -- -- -port {port}",
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

            var uri = await EnsureInitialized(port);

            return new SeleniumServer(process, uri);
        }

        static async ValueTask<Uri> EnsureInitialized(int port)
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(1),
            };

            var uri = new UriBuilder("http", "localhost", port, "/wd/hub").Uri;

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

        public void Dispose()
        {
            SeleniumProcess.Dispose();
        }
    }
}
