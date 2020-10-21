﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web.Test.Common;

namespace Microsoft.Identity.Web.Perf.Client
{
    public class TestRunner
    {
        private readonly TestRunnerOptions _options;
        private readonly string[] _userAccountIdentifiers;
        private TimeSpan elapsedTimeInMsalCacheLookup;

        private readonly DateTime _processingStartTime;
        private readonly DateTime _processingEndTime;
        private int _continueProcessingInt = 1;
        private bool _continueProcessing => _continueProcessingInt == 1;
        private HttpClient _httpClient;

        public TestRunner(TestRunnerOptions options)
        {
            _options = options;
            _processingStartTime = DateTime.Now;
            _processingEndTime = DateTime.Now.AddMinutes(options.RuntimeInMinutes);
            _userAccountIdentifiers = new string[options.NumberOfUsersToTest + _options.StartUserIndex];

            // Configuring the HTTP client to trust the self-signed certificate
            var httpClientHandler = new HttpClientHandler();
            httpClientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            _httpClient = new HttpClient(httpClientHandler);
            _httpClient.BaseAddress = new Uri(options.TestServiceBaseUri);
        }

        public async Task Run()
        {
            Console.WriteLine($"Starting test with {_options.NumberOfUsersToTest} users from {_options.StartUserIndex} to {_options.StartUserIndex + _options.NumberOfUsersToTest - 1}.");

            if (_options.RunIndefinitely)
            {
                Console.WriteLine("Running indefinitely.");
            }
            else
            {
                Console.WriteLine($"Running for {_options.RuntimeInMinutes} minutes.");
            }

            // Try loading from cache
            ScalableTokenCacheHelper.LoadCache();
            IDictionary<int, string> accounts = ScalableTokenCacheHelper.GetAccountIdsByUserNumber();
            foreach (var account in accounts)
            {
                if (account.Key < _userAccountIdentifiers.Length)
                {
                    _userAccountIdentifiers[account.Key] = account.Value;
                }
            }

            IEnumerable<Task> CreateTasks()
            {
                // Checks if processing end time has passed
                yield return Task.Run(
                    async () =>
                    {
                        while (_continueProcessing && DateTime.Now < _processingEndTime)
                        {
                            await Task.Delay(30000);
                        }

                        StopProcessing();
                        Console.WriteLine("Processing stopped. Duration elapsed.");
                    });

                // Checks if stop was requested by user
                yield return Task.Run(
                    async () =>
                    {
                        while (_continueProcessing)
                        {
                            await Task.Delay(1000);

                            if (Console.KeyAvailable)
                            {
                                ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                                if (keyInfo.Key == ConsoleKey.Escape)
                                {
                                    StopProcessing();
                                    Console.WriteLine("Processing stopped. User cancelled.");
                                }
                            }
                        }

                    });

                // Chunk the number of users that needs to be tested and create specified number of tasks
                var chunkSize = _options.NumberOfUsersToTest < _options.NumberOfParallelTasks ? 1 : (_options.NumberOfUsersToTest / _options.NumberOfParallelTasks);
                var numberOfParallelTasks = _options.NumberOfUsersToTest < _options.NumberOfParallelTasks ? _options.NumberOfUsersToTest : _options.NumberOfParallelTasks;
                foreach (int chunkNumber in Enumerable.Range(0, numberOfParallelTasks))
                {
                    var startIndex = chunkNumber * chunkSize + _options.StartUserIndex;
                    var endIndex = startIndex + chunkSize - 1;
                    endIndex = (endIndex + chunkSize) <= _options.StartUserIndex + _options.NumberOfUsersToTest ? endIndex : _options.StartUserIndex + _options.NumberOfUsersToTest - 1;
                    yield return Task.Factory.StartNew(() => SendRequestsAsync(startIndex, endIndex), TaskCreationOptions.LongRunning).Unwrap();
                }
            }

            await Task.WhenAll(CreateTasks());
        }

        private async Task SendRequestsAsync(int userStartIndex, int userEndIndex)
        {
            Console.WriteLine($"Task started for users {userStartIndex} to {userEndIndex}.");

            DateTime startOverall = DateTime.Now;
            TimeSpan elapsedTime = TimeSpan.Zero;
            int requestsCounter = 0;
            int authRequestFailureCount = 0;
            int catchAllFailureCount = 0;
            int tokenReturnedFromCache = 0;
            StringBuilder exceptionsDuringRun = new StringBuilder();


            int loop = 0;
            while (_continueProcessing)
            {
                loop++;
                for (int i = userStartIndex; i <= userEndIndex && _continueProcessing; i++)
                {
                    try
                    {
                        HttpResponseMessage response;
                        using (HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, _options.TestUri))
                        {
                            AuthenticationResult authResult = await AcquireTokenAsync(i);
                            if (authResult == null)
                            {
                                authRequestFailureCount++;
                            }
                            else
                            {
                                httpRequestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                                httpRequestMessage.Headers.Add(
                                    "Authorization",
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "{0} {1}",
                                        "Bearer",
                                        authResult?.AccessToken));

                                DateTime start = DateTime.Now;
                                response = await _httpClient.SendAsync(httpRequestMessage).ConfigureAwait(false);
                                elapsedTime += DateTime.Now - start;
                                requestsCounter++;
                                if (authResult?.AuthenticationResultMetadata.TokenSource == TokenSource.Cache)
                                {
                                    tokenReturnedFromCache++;
                                }
                                else
                                {
                                    if (i % 10 == 0)
                                    {
                                        ScalableTokenCacheHelper.PersistCache();
                                    }
                                }

                                if (!response.IsSuccessStatusCode)
                                {
                                    var sb = new StringBuilder();
                                    sb.AppendLine($"Response was not successful. Status code: {response.StatusCode}. {response.ReasonPhrase}");
                                    sb.AppendLine(await response.Content.ReadAsStringAsync());
                                    Console.WriteLine(sb);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        catchAllFailureCount++;
                        Console.WriteLine($"Exception in TestRunner at {i} of {userEndIndex} - {userStartIndex}: {ex.Message}");

                        exceptionsDuringRun.AppendLine($"Exception in TestRunner at {i} of {userEndIndex} - {userStartIndex}: {ex.Message}");
                        exceptionsDuringRun.AppendLine($"{ex}");
                    }
                }

                UpdateConsoleProgress(startOverall, elapsedTime, requestsCounter, tokenReturnedFromCache, authRequestFailureCount, catchAllFailureCount, userStartIndex, userEndIndex, loop);

                ScalableTokenCacheHelper.PersistCache();

                //File.AppendAllText(System.Reflection.Assembly.GetExecutingAssembly().Location + ".exceptions.log", exceptionsDuringRun.ToString());
            }
        }

        private void StopProcessing() => Interlocked.Exchange(ref _continueProcessingInt, 0);

        private void UpdateConsoleProgress(DateTime startOverall, TimeSpan elapsedTime, int requestsCounter, int tokenReturnedFromCache, 
            int authRequestFailureCount, int catchAllFailureCount, int userStartIndex, int userEndIndex, int loop)
        {
            var sb = new StringBuilder();
            sb.AppendLine("------------------------------------------------------------------------");
            sb.AppendLine($"Runtime: {startOverall} - {DateTime.Now} = {DateTime.Now - startOverall}");
            sb.AppendLine($"Loop: {loop}");
            sb.AppendLine($"WebAPI Time: {elapsedTime}");
            sb.AppendLine($"Total number of users: {userEndIndex - userStartIndex}. [{userEndIndex} - {userStartIndex}]");
            sb.AppendLine($"AuthRequest Failures: {authRequestFailureCount}. Generic failures: {catchAllFailureCount}");
            sb.AppendLine($"Total requests: {requestsCounter}, avg. time per request: {(elapsedTime.TotalSeconds / requestsCounter):0.0000}");
            sb.AppendLine($"Cache requests: {tokenReturnedFromCache}. Avg. cache time: {(elapsedTimeInMsalCacheLookup.TotalSeconds / tokenReturnedFromCache):0.0000}. (Total: {elapsedTimeInMsalCacheLookup})");
            Console.WriteLine(sb);
        }

        private async Task<AuthenticationResult> AcquireTokenAsync(int userIndex)
        {
            var scopes = new string[] { _options.ApiScopes };
            var upn = $"{_options.UsernamePrefix}{userIndex}@{_options.TenantDomain}";

            var builder = PublicClientApplicationBuilder
                           .Create(_options.ClientId)
                           .WithAuthority(TestConstants.AadInstance, TestConstants.Organizations);

            if (_options.EnableMsalLogging)
            {
                builder.WithLogging(Log, LogLevel.Info, false);
            }

            var msalPublicClient = builder.Build();

            ScalableTokenCacheHelper.EnableSerialization(msalPublicClient.UserTokenCache);

            AuthenticationResult authResult = null;
            try
            {
                try
                {
                    var identifier = _userAccountIdentifiers[userIndex];
                    IAccount account = null;
                    if (identifier != null)
                    {
                        DateTime start = DateTime.Now;
                        account = await msalPublicClient.GetAccountAsync(identifier).ConfigureAwait(false);
                        elapsedTimeInMsalCacheLookup += DateTime.Now - start;
                    }

                    authResult = await msalPublicClient.AcquireTokenSilent(scopes, account).ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
                    return authResult;
                }
                catch (MsalUiRequiredException)
                {
                    authResult = await msalPublicClient.AcquireTokenByUsernamePassword(
                                                        scopes,
                                                        upn,
                                                        new NetworkCredential(
                                                            upn,
                                                            _options.UserPassword).SecurePassword)
                                                        .ExecuteAsync(CancellationToken.None)
                                                        .ConfigureAwait(false);

                    _userAccountIdentifiers[userIndex] = authResult.Account.HomeAccountId.Identifier;
                    return authResult;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in AcquireTokenAsync: {ex}");
            }
            return authResult;
        }

        private static string s_msallogfile = System.Reflection.Assembly.GetExecutingAssembly().Location + ".msalLogs.txt";
        private static StringBuilder s_log = new StringBuilder();
        private static volatile bool s_isLogging = false;
        private static object s_logLock = new object();

        private static void Log(LogLevel level, string message, bool containsPii)
        {
            StringBuilder tempBuilder = new StringBuilder();
            bool writeToDisk = false;
            lock (s_logLock)
            {
                string logs = ($"{level} {message}");
                if (!s_isLogging)
                {
                    s_isLogging = true;
                    writeToDisk = true;
                    tempBuilder.Append(s_log);
                    tempBuilder.Append(logs);
                    s_log.Clear();
                }
                else
                {
                    s_log.Append(logs);
                }
            }

            if (!writeToDisk)
            {
                return;
            }

            s_isLogging = true;
            try
            {
                File.AppendAllText(s_msallogfile, tempBuilder.ToString());
                tempBuilder.Clear();
            }
            finally
            {
                s_isLogging = false;
            }
        }
    }
}
