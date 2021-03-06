/*
 * LambdaSharp (λ#)
 * Copyright (C) 2018-2020
 * lambdasharp.net
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Amazon.CloudWatchEvents;
using Amazon.CloudWatchEvents.Model;
using Amazon.DynamoDBv2;
using Amazon.Lambda.KinesisFirehoseEvents;
using LambdaSharp.Core.Registrations;
using LambdaSharp.Core.RollbarApi;
using LambdaSharp.Logging;
using LambdaSharp.Logging.ErrorReports.Models;
using LambdaSharp.Logging.Events.Models;
using LambdaSharp.Logging.Metrics;
using LambdaSharp.Logging.Metrics.Models;

namespace LambdaSharp.Core.LoggingStreamAnalyzerFunction {

    public class LogEventsMessage {

        //--- Properties ---
        public string? Owner { get; set; }
        public string? LogGroup { get; set; }
        public string? LogStream { get; set; }
        public string? MessageType { get; set; }
        public List<string>? SubscriptionFilters { get; set; }
        public List<LogEventEntry>? LogEvents { get; set; }
    }

    public class LogEventEntry {

        //--- Properties ---
        public string? Id { get; set; }
        public long Timestamp { get; set; }
        public string? Message { get; set; }
    }

    public class LogRecord {

        //--- Properties ---
        public long Timestamp { get; set; }
        public string? ModuleInfo { get; set; }
        public string? Module { get; set; }
        public string? ModuleId { get; set; }
        public string? Function { get; set; }
        public string? FunctionId { get; set; }
        public string? Tier { get; set; }
        public string? RecordType { get; set; }
        public string? Record { get; set; }
    }

    public sealed class Function : ALambdaFunction<KinesisFirehoseEvent, KinesisFirehoseResponse>, ILogicDependencyProvider {

        //--- Constants ---
        private const string LAMBDA_LOG_GROUP_PREFIX = "/aws/lambda/";
        private const int MAX_EVENTS_BATCHSIZE = 256 * 1024;
        private const int MAX_EVENTS_COUNT = 10;

        //--- Fields ---
        private Logic? _logic;
        private RegistrationTable? _registrations;
        private Dictionary<string, OwnerMetaData>? _cachedRegistrations;
        private RollbarClient? _rollbarClient;
        private int _errorsReportsCount;
        private int _warningsReportsCount;
        private IAmazonCloudWatchEvents? _eventsClient;
        private List<PutEventsRequestEntry> _eventEntries = new List<PutEventsRequestEntry>();
        private int _eventsEntriesTotalSize = 0;
        private OwnerMetaData? _selfMetaData;
        private List<string> _convertedRecords = new List<string>();

        //--- Properties ---
        private Logic Logic => _logic ?? throw new InvalidOperationException();
        private IAmazonCloudWatchEvents EventsClient => _eventsClient ?? throw new InvalidOperationException();
        private Dictionary<string, OwnerMetaData> CachedRegistrations => _cachedRegistrations ?? throw new InvalidOperationException();
        private RollbarClient RollbarClient => _rollbarClient ?? throw new InvalidOperationException();
        private RegistrationTable Registrations => _registrations ?? throw new InvalidOperationException();
        private OwnerMetaData SelfMetaData => _selfMetaData ?? throw new InvalidOperationException();

        //--- Methods ---
        public override async Task InitializeAsync(LambdaConfig config) {
            _logic = new Logic(this);
            var tableName = config.ReadDynamoDBTableName("RegistrationTable");
            var dynamoClient = new AmazonDynamoDBClient();
            _registrations = new RegistrationTable(dynamoClient, tableName);
            _cachedRegistrations = new Dictionary<string, OwnerMetaData>();
            _rollbarClient = new RollbarClient(null, null, message => LogInfo(message));
            _eventsClient = new AmazonCloudWatchEventsClient();
            _selfMetaData = new OwnerMetaData {
                ModuleInfo = Info.ModuleInfo,
                Module = Info.ModuleFullName,
                ModuleId = Info.ModuleId,
                FunctionId = Info.FunctionId,
                FunctionName = Info.FunctionName,
                FunctionLogGroupName = CurrentContext.LogGroupName,
                FunctionPlatform = "AWS Lambda",
                FunctionFramework = Info.FunctionFramework,
                FunctionLanguage = "csharp"
            };
        }

        public override async Task<KinesisFirehoseResponse> ProcessMessageAsync(KinesisFirehoseEvent request) {

            // NOTE (2018-12-11, bjorg): this function is responsible for error logs parsing; therefore, it CANNOT error out itself;
            //  instead, it must rely on aggressive exception handling and redirect those message where appropriate.

            ResetMetrics();
            var response = new KinesisFirehoseResponse {
                Records = new List<KinesisFirehoseResponse.FirehoseRecord>()
            };
            try {
                foreach(var record in request.Records) {
                    try {

                        // deserialize kinesis record into a CloudWatch Log event
                        LogEventsMessage logEvent;
                        using(var sourceStream = new MemoryStream(Convert.FromBase64String(record.Base64EncodedData)))
                        using(var destinationStream = new MemoryStream()) {
                            using(var gzip = new GZipStream(sourceStream, CompressionMode.Decompress)) {
                                gzip.CopyTo(destinationStream);
                                destinationStream.Position = 0;
                            }
                            logEvent = LambdaSerializer.Deserialize<LogEventsMessage>(Encoding.UTF8.GetString(destinationStream.ToArray()));
                        }

                        // validate log event
                        if(
                            (logEvent.LogGroup == null)
                            || (logEvent.MessageType == null)
                            || (logEvent.LogEvents == null)
                        ) {
                            LogWarn("invalid record (record-id: {0})", record.RecordId);
                            RecordFailed(record);
                            continue;
                        }

                        // skip log event from own module
                        if(logEvent.LogGroup.Contains(Info.FunctionName)) {
                            LogInfo("skipping event from own event log (record-id: {0})", record.RecordId);
                            RecordDropped(record);
                            continue;
                        }

                        // skip control log event
                        if(logEvent.MessageType == "CONTROL_MESSAGE") {
                            LogInfo("skipping control message (record-id: {0})", record.RecordId);
                            RecordDropped(record);
                            continue;
                        }

                        // check if this log event is expected
                        if(logEvent.MessageType != "DATA_MESSAGE") {
                            LogWarn("unknown message type '{1}' (record-id: {0})", record.RecordId, logEvent.MessageType);
                            RecordFailed(record);
                            continue;
                        }

                        // check if log group belongs to a Lambda function
                        OwnerMetaData? owner;
                        if(logEvent.LogGroup.StartsWith(LAMBDA_LOG_GROUP_PREFIX, StringComparison.Ordinal)) {

                            // use CloudWatch log name to identify owner of the log event
                            var functionId = logEvent.LogGroup.Substring(LAMBDA_LOG_GROUP_PREFIX.Length);
                            owner = await GetOwnerMetaDataAsync($"F:{functionId}");
                        } else {
                            owner = await GetOwnerMetaDataAsync($"L:{logEvent.LogGroup}");
                        }

                        // check if owner record exists
                        if(owner == null) {
                            LogInfo("skipping record for non-registered log-group (record-id: {0}, log-group: {1})", record.RecordId, logEvent.LogGroup);
                            RecordDropped(record);
                            continue;
                        }

                        // process entries in log event
                        _convertedRecords.Clear();
                        var success = true;
                        var logEventIndex = -1;
                        foreach(var entry in logEvent.LogEvents) {
                            ++logEventIndex;
                            try {
                                await Logic.ProgressLogEntryAsync(
                                    owner,
                                    entry.Message,
                                    DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp)
                                );
                            } catch(Exception e) {
                                if(owner.FunctionId != null) {
                                    LogError(e, "log event entry [{1}] processing failed (function-id: {3}, record-id: {0}):\n{2}", record.RecordId, logEventIndex, entry.Message, owner.FunctionId);
                                } else if(owner.AppId != null) {
                                    LogError(e, "log event entry [{1}] processing failed (app-id: {3}, record-id: {0}):\n{2}", record.RecordId, logEventIndex, entry.Message, owner.AppId);
                                } else {
                                    LogError(e, "log event entry [{1}] processing failed (log-group: {3}, record-id: {0}):\n{2}", record.RecordId, logEventIndex, entry.Message, logEvent.LogGroup);
                                }
                                success = false;
                                break;
                            }
                        }

                        // record how long it took to process the CloudWatch Log event
                        if(logEvent.LogEvents.Any()) {
                            try {
                                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(logEvent.LogEvents.First().Timestamp);
                                LogMetric("LogEvent.Latency", (DateTimeOffset.UtcNow - timestamp).TotalMilliseconds, LambdaMetricUnit.Milliseconds);
                            } catch(Exception e) {
                                LogError(e, "report log event latency failed");
                            }
                        }

                        // record outcome
                        if(success) {
                            if(_convertedRecords.Any()) {
                                LogInfo($"finished log events record (converted {_convertedRecords.Count:N0}, skipped {logEvent.LogEvents.Count - _convertedRecords.Count:N0}, record-id: {record.RecordId})");
                                RecordSuccess(record, _convertedRecords.Aggregate("", (accumulator, convertedRecord) => accumulator + convertedRecord + "\n"));
                            } else {
                                LogInfo($"dropped record (record-id: {record.RecordId}");
                                RecordDropped(record);
                            }
                        } else {

                            // nothing to log since error was already logged
                            RecordFailed(record);
                        }
                    } catch(Exception e) {
                        LogError(e, "record failed (record-id: {0})", record.RecordId);
                        RecordFailed(record);
                    }
                }
            } finally {

                // NOTE (2020-04-21, bjorg): we don't expect this to fail; but since it's done at the end of the processing function, we
                //  need to make sure it never fails; otherwise, the Kinesis stream processing is interrupted.
                try {
                    ReportMetrics();
                } catch(Exception e) {
                    LogError(e, "report metrics failed");
                }

                // send accumulated events
                try {
                    PurgeEventEntries();
                } catch(Exception exception) {
                    Provider.Log($"EXCEPTION: {exception}\n");
                }
            }
            return response;

            // local functions
            void RecordSuccess(KinesisFirehoseEvent.FirehoseRecord record, string data)
                => response.Records.Add(new KinesisFirehoseResponse.FirehoseRecord {
                    RecordId = record.RecordId,
                    Result = KinesisFirehoseResponse.TRANSFORMED_STATE_OK,
                    Base64EncodedData = Convert.ToBase64String(Encoding.UTF8.GetBytes(data))
                });

            void RecordFailed(KinesisFirehoseEvent.FirehoseRecord record)
                => response.Records.Add(new KinesisFirehoseResponse.FirehoseRecord {
                    RecordId = record.RecordId,
                    Result = KinesisFirehoseResponse.TRANSFORMED_STATE_PROCESSINGFAILED
                });

            void RecordDropped(KinesisFirehoseEvent.FirehoseRecord record)
                => response.Records.Add(new KinesisFirehoseResponse.FirehoseRecord {
                    RecordId = record.RecordId,
                    Result = KinesisFirehoseResponse.TRANSFORMED_STATE_DROPPED
                });
        }

        private async Task<OwnerMetaData?> GetOwnerMetaDataAsync(string functionId) {

            // check if the owner has already been retrieved previously
            OwnerMetaData? result;
            if(!CachedRegistrations.TryGetValue(functionId, out result)) {
                result = await Registrations.GetOwnerMetaDataAsync(functionId);
                if(result != null) {

                    // check if a Rollbar access token is present that needs to be decrypted
                    if(result.RollbarAccessToken != null) {
                        result.RollbarAccessToken = await DecryptSecretAsync(result.RollbarAccessToken);
                    }

                    // cache owner record for future look-up
                    CachedRegistrations[functionId] = result;
                }
            }
            return result;
        }

        private async Task PublishErrorReportToRollbarAsync(OwnerMetaData owner, LambdaErrorReport report) {
            if(owner.RollbarAccessToken == null) {
                return;
            }

            // convert error report into rollbar data structure
            var rollbar = new Rollbar {
                AccessToken = owner.RollbarAccessToken,
                Data = new Data {
                    Environment = string.IsNullOrEmpty(Info.DeploymentTier)
                        ? "<DEFAULT>"
                        : Info.DeploymentTier,
                    Level = report.Level?.ToLowerInvariant() switch {
                        "error" => "error",
                        "warning" => "warning",
                        "fatal" => "critical",
                        _ => "error"
                    },
                    Timestamp = report.Timestamp,
                    CodeVersion = report.GitSha,
                    Platform = report.Platform,
                    Language = report.Language,
                    Framework = report.Framework,
                    Fingerprint = report.Fingerprint,
                    Title = $"{report.FunctionName ?? report.AppName}: {report.Message}",
                    Custom = new {
                        Message = report.Message,
                        ModuleInfo = report.ModuleInfo,
                        Module = report.Module,
                        ModuleId = report.ModuleId,
                        FunctionId = report.FunctionId,
                        FunctionName = report.FunctionName,
                        GitBranch = report.GitBranch,
                        RequestId = report.RequestId,
                        AppId = report.AppId,
                        AppName = report.AppName
                    },
                    Body = new DataBody {
                        TraceChain = report.Traces?.Select(trace => new Trace {
                            Exception = new ExceptionClass {
                                Class = trace.Exception?.Type,
                                Message = trace.Exception?.Message,
                                Description = trace.Exception?.StackTrace
                            },
                            Frames = trace.Frames?.Select(frame => new Frame {
                                Filename = frame.FileName,
                                Lineno = frame.LineNumber.GetValueOrDefault(),
                                Method = frame.MethodName
                            }).ToArray()
                        }).ToArray()
                    }
                }
            };

            // in case there are no captured traces, inject a simple error message
            if(rollbar.Data.Body.TraceChain?.Any() != true) {
                rollbar.Data.Body.TraceChain = null;
                rollbar.Data.Body = new DataBody {
                    Message = new Message {
                        Body = report.Raw
                    }
                };
            }

            // send payload to rollbar
            try {
                var response = RollbarClient.SendRollbarPayload(rollbar);
                LogInfo($"Rollbar.SendRollbarPayload() succeeded: {response}");
            } catch(WebException e) {
                if(e.Response == null) {
                    LogWarn($"Rollbar request failed (status: {e.Status}, message: {e.Message})");
                } else {
                    using(var stream = e.Response.GetResponseStream()) {
                        if(stream == null) {
                            LogWarn($"Rollbar.SendRollbarPayload() failed: {e.Status}");
                        } else {
                            using(var reader = new StreamReader(stream)) {
                                LogWarn($"Rollbar.SendRollbarPayload() failed: {reader.ReadToEnd()}");
                            }
                        }
                    }
                }
            } catch(Exception e) {
                LogErrorAsWarning(e, "Rollbar.SendRollbarPayload() failed");
            }
        }

        private void ResetMetrics() {
            _errorsReportsCount = 0;
            _warningsReportsCount = 0;
        }

        private void ReportMetrics() {
            var metrics = new List<LambdaMetric>();
            if(_errorsReportsCount > 0) {
                metrics.Add(("LambdaError.Errors.Count", _errorsReportsCount, LambdaMetricUnit.Count));
            }
            if(_warningsReportsCount > 0) {
                metrics.Add(("LambdaError.Warnings.Count", _warningsReportsCount, LambdaMetricUnit.Count));
            }
            LogMetric(metrics);
        }

        private void PurgeEventEntries() {
            if(_eventEntries.Count > 0) {
                var eventEntries = _eventEntries;

                // send accumulated events
                RunTask(async () => {

                    // send events
                    try {
                        await EventsClient.PutEventsAsync(new PutEventsRequest {
                            Entries = eventEntries
                        });
                    } catch(Exception e) {
                        LogErrorAsWarning(e, "EventsClient.PutEvents() failed");
                    }
                });

                // reset events accumulator
                _eventEntries = new List<PutEventsRequestEntry>();
                _eventsEntriesTotalSize = 0;
            }
        }

        private void AddEventEntry(PutEventsRequestEntry entry) {

            // calculate size of new event entry
            var entrySize = GetEventEntrySize(entry);
            if((_eventsEntriesTotalSize + entrySize) > MAX_EVENTS_BATCHSIZE) {
                PurgeEventEntries();
            }
            _eventEntries.Add(entry);
            if(_eventEntries.Count == MAX_EVENTS_COUNT) {
                PurgeEventEntries();
            }

            // local functions
            int GetEventEntrySize(PutEventsRequestEntry eventEntry) {
                var size = 0;
                if(eventEntry.Time != null) {
                    size += 14;
                }
                size += GetUtf8Length(eventEntry.Source);
                size += GetUtf8Length(eventEntry.DetailType);
                size += GetUtf8Length(eventEntry.Detail);
                foreach(var resource in eventEntry.Resources) {
                    size += GetUtf8Length(resource);
                }
                return size;
            }

            int GetUtf8Length(string? text) => (text != null) ? Encoding.UTF8.GetByteCount(text) : 0;
        }

        private void SendEventRecord(OwnerMetaData owner, LambdaEventRecord record) {
            var resources = new List<string> {
                $"lambdasharp:stack:{owner?.ModuleId}",
                $"lambdasharp:module:{owner?.Module}",
                $"lambdasharp:tier:{Info.DeploymentTier}"
            };

            // add module info and origin when possible
            if(owner?.ModuleInfo != null) {

                // add module info
                resources.Add($"lambdasharp:moduleinfo:{owner.ModuleInfo}");

                // pare module info to extract origin information
                var nameAndVersionOwner = owner.ModuleInfo.Split(':', 2);
                var versionAndOwner = (nameAndVersionOwner.Length == 2)
                    ? nameAndVersionOwner[1].Split('@', 2)
                    : new[] { nameAndVersionOwner[0] };
                if((versionAndOwner.Length == 2) && !string.IsNullOrEmpty(versionAndOwner[1])) {

                    // add module origin
                    resources.Add($"lambdasharp:origin:{versionAndOwner[1]}");
                }
            }
            if(record.Resources != null) {
                resources.AddRange(resources);
            }
            var entry = new PutEventsRequestEntry {
                Source = record.Source,
                DetailType = record.DetailType,
                Detail = record.Detail,
                Resources = resources
            };
            if(DateTime.TryParse(record.Time, out var timestamp)) {
                entry.Time = timestamp;
            } else {
                entry.Time = DateTime.UtcNow;
            }
            AddEventEntry(entry);
        }

        protected override void RecordErrorReport(LambdaErrorReport report) {
            base.RecordErrorReport(report);

            // publish error report to the event bus
            try {
                SendEventRecord(SelfMetaData, new LambdaEventRecord {
                    Source =  "LambdaSharp",
                    DetailType = "LambdaError",
                    Detail = LambdaSerializer.Serialize(report)
                });
            } catch {

                // nothing to do; error report was already sent to logs
            }
        }

        protected override void RecordException(Exception exception) {

            // NOTE (2020-05-05, bjorg): RecordException(Exception) is ONLY invoked when
            //  RecordErrorReport(LambdaErrorReport) fails. Therefore, this method MUST remain
            //  as basic as possible.
            base.RecordException(exception);

            // publish exception to the event bus
            try {
                SendEventRecord(SelfMetaData, new LambdaEventRecord {
                    Source = "LambdaSharp",
                    DetailType = "Exception",
                    Detail = LambdaSerializer.Serialize(new {
                        Message = exception?.Message,
                        Type = exception?.GetType().FullName,
                        Raw = exception?.ToString()
                    })
                });
            } catch {

                // nothing to do; error report was already sent to logs
            }
        }

        private void AddConvertedRecord(OwnerMetaData owner, DateTimeOffset timestamp, ALambdaLogRecord record)
            => _convertedRecords.Add(LambdaSerializer.Serialize(new LogRecord {
                Timestamp = timestamp.ToUnixTimeMilliseconds(),
                ModuleInfo = owner.ModuleInfo,
                Module = owner.Module,
                ModuleId = owner.ModuleId,
                Function = owner.FunctionName,
                FunctionId = owner.FunctionId,
                Tier = Info.DeploymentTier,
                RecordType = record.Type,
                Record = LambdaSerializer.Serialize<object>(record)
            }));

        //--- ILogicDependencyProvider Members ---
        async Task ILogicDependencyProvider.SendErrorReportAsync(OwnerMetaData owner, DateTimeOffset timestamp, LambdaErrorReport report) {

            // send parsed error report to event bus
            var eventRecord = new LambdaEventRecord {
                Source = "LambdaSharp",
                DetailType = "LambdaError",
                Detail = LambdaSerializer.Serialize(report)
            };
            eventRecord.SetTime(timestamp);
            SendEventRecord(owner, eventRecord);

            // capture reporting metrics
            switch(report.Level) {
            case "ERROR":
                ++_errorsReportsCount;
                break;
            case "WARNING":
                ++_warningsReportsCount;
                break;
            }

            // capture error report as converted record
            AddConvertedRecord(owner, timestamp, report);

            // publish error report to Rollbar
            try {
                await PublishErrorReportToRollbarAsync(owner, report);
            } catch(Exception e) {
                LogErrorAsWarning(e, "failed sending error report to Rollbar");
            }
        }

        async Task ILogicDependencyProvider.SendUsageReportAsync(OwnerMetaData owner, DateTimeOffset timestamp, LambdaUsageRecord report) {

            // publish usage report to the event bus
            SendEventRecord(owner, new LambdaEventRecord {
                Source = "LambdaSharp",
                DetailType = "LambdaUsage",
                Detail = LambdaSerializer.Serialize(report)
            });

            // capture usage report as converted record
            AddConvertedRecord(owner, timestamp, report);
        }

        async Task ILogicDependencyProvider.SendEventAsync(OwnerMetaData owner, DateTimeOffset timestamp, LambdaEventRecord record) {

            // capture event as converted record
            AddConvertedRecord(owner, timestamp, record);
        }

        async Task ILogicDependencyProvider.SendMetricsAsync(OwnerMetaData owner, DateTimeOffset timestamp, LambdaMetricsRecord record) {

            // publish metrics to the event bus
            var eventRecord = new LambdaEventRecord {
                Source = "LambdaSharp",
                DetailType = "LambdaMetrics",
                Detail = LambdaSerializer.Serialize(record)
            };
            eventRecord.SetTime(DateTimeOffset.FromUnixTimeMilliseconds(record.Aws.Timestamp));
            SendEventRecord(owner, eventRecord);

            // capture metrics as converted record
            AddConvertedRecord(owner, timestamp, record);
        }
    }
}
