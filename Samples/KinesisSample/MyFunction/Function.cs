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

using System.Threading.Tasks;
using Amazon.Lambda.KinesisEvents;
using LambdaSharp;

namespace KinesisSample.MyFunction {

    public class Function : ALambdaFunction<KinesisEvent, string> {

        //--- Methods ---
        public override Task InitializeAsync(LambdaConfig config)
            => Task.CompletedTask;

        public override async Task<string> ProcessMessageAsync(KinesisEvent evt) {
            LogInfo($"# Kinesis Records = {evt.Records.Count}");
            for(var i = 0; i < evt.Records.Count; ++i) {
                var record = evt.Records[i];
                LogInfo($"Record #{i}");
                LogInfo($"AwsRegion = {record.AwsRegion}");
                LogInfo($"EventId = {record.EventId}");
                LogInfo($"EventName = {record.EventName}");
                LogInfo($"EventSource = {record.EventSource}");
                LogInfo($"EventSourceARN = {record.EventSourceARN}");
                LogInfo($"EventVersion = {record.EventVersion}");
                LogInfo($"InvokeIdentityArn = {record.InvokeIdentityArn}");
                LogInfo($"ApproximateArrivalTimestamp = {record.Kinesis.ApproximateArrivalTimestamp}");
                LogInfo($"Kinesis.Data.Length = {record.Kinesis.Data.Length}");
                LogInfo($"Kinesis.KinesisSchemaVersion = {record.Kinesis.KinesisSchemaVersion}");
                LogInfo($"KinesisPartitionKey = {record.Kinesis.PartitionKey}");
                LogInfo($"KinesisSequenceNumber = {record.Kinesis.SequenceNumber}");
            }
            return "Ok";
        }
    }
}