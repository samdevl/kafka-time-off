﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Configuration;
using TimeOff.Core;
using TimeOff.Models;

namespace TimeOff.Manager
{
    public record KafkaMessage(string Key, int Partition, LeaveApplicationReceived Message);

    internal class Program
    {
        private static ConfigReader _configReader;
        private static AdminClientConfig _adminConfig;
        private static SchemaRegistryConfig _schemaRegistryConfig;
        private static ConsumerConfig _consumerConfig;
        private static ProducerConfig _producerConfig;

        private static Queue<KafkaMessage> _leaveApplicationReceivedMessages;

        public static IConfiguration Configuration { get; private set; }

        private static async Task Main(string[] args)
        {
            Console.WriteLine("TimeOff Manager Terminal\n");

            Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true, true)
                .Build();
            
            _adminConfig = Configuration.GetSection(nameof(AdminClientConfig)).Get<AdminClientConfig>();
            _schemaRegistryConfig = Configuration.GetSection(nameof(SchemaRegistryConfig)).Get<SchemaRegistryConfig>();
            _consumerConfig = Configuration.GetSection(nameof(ConsumerConfig)).Get<ConsumerConfig>();
            // Read messages from start if no commit exists.
            _consumerConfig.AutoOffsetReset = AutoOffsetReset.Earliest;
            _producerConfig = Configuration.GetSection(nameof(ProducerConfig)).Get<ProducerConfig>();
            _producerConfig.ClientId = Dns.GetHostName();

            /*_configReader = new ConfigReader(Configuration);

            // Read configs
            _schemaRegistryConfig = _configReader.GetSchemaRegistryConfig();
            _consumerConfig = _configReader.GetConsumerConfig();
            _producerConfig = _configReader.GetProducerConfig();

            // Azure EH does not support Kafka Admin APIs.
            if (_configReader.IsLocalEnvironment)
            {
                _adminConfig = _configReader.GetAdminConfig();
                await KafkaHelper.CreateTopicAsync(_adminConfig, ApplicationConstants.LeaveApplicationResultsTopicName,
                    1);
            }*/
            await KafkaHelper.CreateTopicAsync(_adminConfig, ApplicationConstants.LeaveApplicationResultsTopicName, 1);
            _leaveApplicationReceivedMessages = new Queue<KafkaMessage>();
            await Task.WhenAny(Task.Run(StartManagerConsumer), Task.Run(StartLeaveApplicationProcessor));
        }

        private static async Task StartLeaveApplicationProcessor()
        {
            while (true)
            {
                if (!_leaveApplicationReceivedMessages.Any())
                {
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                    continue;
                }

                var (key, partition, leaveApplication) = _leaveApplicationReceivedMessages.Dequeue();
                Console.WriteLine(
                    $"Received message: {key} from partition: {partition} Value: {JsonSerializer.Serialize(leaveApplication)}");

                // Make decision on leave request.
                var isApproved = ReadLine.Read("Approve request? (Y/N): ", "Y").Equals("Y", StringComparison.OrdinalIgnoreCase);
                await SendMessageToResultTopicAsync(leaveApplication, isApproved, partition);
            }

            // ReSharper disable once FunctionNeverReturns
        }

        private static Task StartManagerConsumer()
        {
            using var schemaRegistry = new CachedSchemaRegistryClient(_schemaRegistryConfig);
            /*
            CachedSchemaRegistryClient cachedSchemaRegistryClient = null;
            KafkaAvroDeserializer<string> kafkaAvroKeyDeserializer = null;
            KafkaAvroDeserializer<LeaveApplicationReceived> kafkaAvroValueDeserializer = null;

            if (_configReader.IsLocalEnvironment)
            {
                cachedSchemaRegistryClient = new CachedSchemaRegistryClient(_schemaRegistryConfig);
            }
            else
            {
                var schemaRegistryClientAz =
                    new SchemaRegistryClient(Configuration["SchemaRegistryUrlAz"], new DefaultAzureCredential());
                var schemaGroupName = Configuration["SchemaRegistryGroupNameAz"];
                kafkaAvroKeyDeserializer =
                    new KafkaAvroDeserializer<string>(schemaRegistryClientAz, schemaGroupName);
                kafkaAvroValueDeserializer =
                    new KafkaAvroDeserializer<LeaveApplicationReceived>(schemaRegistryClientAz, schemaGroupName);
            }
            */

            using var consumer = new ConsumerBuilder<string, LeaveApplicationReceived>(_consumerConfig)
                .SetKeyDeserializer(new AvroDeserializer<string>(schemaRegistry).AsSyncOverAsync())
                .SetValueDeserializer(new AvroDeserializer<LeaveApplicationReceived>(schemaRegistry).AsSyncOverAsync())
                // .SetKeyDeserializer(
                //     _configReader.IsLocalEnvironment
                //         ? new AvroDeserializer<string>(cachedSchemaRegistryClient).AsSyncOverAsync()
                //         : kafkaAvroKeyDeserializer)
                // .SetValueDeserializer(_configReader.IsLocalEnvironment
                //     ? new AvroDeserializer<LeaveApplicationReceived>(cachedSchemaRegistryClient).AsSyncOverAsync()
                //     : kafkaAvroValueDeserializer)
                .SetErrorHandler((_, e) => Console.WriteLine($"Error: {e.Reason}"))
                .Build();
            {
                try
                {
                    consumer.Subscribe(ApplicationConstants.LeaveApplicationsTopicName);
                    Console.WriteLine("Consumer loop started...\n");
                    while (true)
                    {
                        try
                        {
                            var result =
                                consumer.Consume(
                                    TimeSpan.FromMilliseconds(_consumerConfig.MaxPollIntervalMs - 1000 ?? 250000));
                            var leaveRequest = result?.Message?.Value;
                            if (leaveRequest == null)
                            {
                                continue;
                            }

                            // Adding message to a list just for the demo.
                            // You should persist the message in database and process it later.
                            _leaveApplicationReceivedMessages.Enqueue(new KafkaMessage(result.Message.Key,
                                result.Partition.Value, result.Message.Value));

                            consumer.Commit(result);
                            consumer.StoreOffset(result);
                        }
                        catch (ConsumeException e) when (!e.Error.IsFatal)
                        {
                            Console.WriteLine($"Non fatal error: {e}");
                        }
                    }
                }
                finally
                {
                    consumer.Close();
                }
            }
        }

        private static async Task SendMessageToResultTopicAsync(LeaveApplicationReceived leaveRequest, bool isApproved,
            int partitionId
        )
        {
            using var schemaRegistry = new CachedSchemaRegistryClient(_schemaRegistryConfig);
            /*CachedSchemaRegistryClient cachedSchemaRegistryClient = null;
            KafkaAvroAsyncSerializer<string> kafkaAvroAsyncKeySerializer = null;
            KafkaAvroAsyncSerializer<LeaveApplicationProcessed> kafkaAvroAsyncValueSerializer = null;

            if (_configReader.IsLocalEnvironment)
            {
                cachedSchemaRegistryClient = new CachedSchemaRegistryClient(_schemaRegistryConfig);
            }
            else
            {
                var schemaRegistryClientAz =
                    new SchemaRegistryClient(Configuration["SchemaRegistryUrlAz"], new DefaultAzureCredential());
                var schemaGroupName = Configuration["SchemaRegistryGroupNameAz"];
                kafkaAvroAsyncKeySerializer =
                    new KafkaAvroAsyncSerializer<string>(schemaRegistryClientAz, schemaGroupName);
                kafkaAvroAsyncValueSerializer =
                    new KafkaAvroAsyncSerializer<LeaveApplicationProcessed>(schemaRegistryClientAz, schemaGroupName);
            }*/

            using var producer = new ProducerBuilder<string, LeaveApplicationProcessed>(_producerConfig)
                .SetKeySerializer(new AvroSerializer<string>(schemaRegistry))
                .SetValueSerializer(new AvroSerializer<LeaveApplicationProcessed>(schemaRegistry))
                // .SetKeySerializer(_configReader.IsLocalEnvironment
                //     ? new AvroSerializer<string>(cachedSchemaRegistryClient)
                //     : kafkaAvroAsyncKeySerializer)
                // .SetValueSerializer(_configReader.IsLocalEnvironment
                //     ? new AvroSerializer<LeaveApplicationProcessed>(cachedSchemaRegistryClient)
                //     : kafkaAvroAsyncValueSerializer)
                .Build();
            {
                var leaveApplicationResult = new LeaveApplicationProcessed
                {
                    EmpDepartment = leaveRequest.EmpDepartment,
                    EmpEmail = leaveRequest.EmpEmail,
                    LeaveDurationInHours = leaveRequest.LeaveDurationInHours,
                    LeaveStartDateTicks = leaveRequest.LeaveStartDateTicks,
                    ProcessedBy = $"Manager #{partitionId}",
                    Result = isApproved
                        ? "Approved: Your leave application has been approved."
                        : "Declined: Your leave application has been declined."
                };

                var result = await producer.ProduceAsync(ApplicationConstants.LeaveApplicationResultsTopicName,
                    new Message<string, LeaveApplicationProcessed>
                    {
                        Key = $"{leaveRequest.EmpEmail}-{DateTime.UtcNow.Ticks}",
                        Value = leaveApplicationResult
                    });
                Console.WriteLine(
                    $"\nMsg: Leave request processed and queued at offset {result.Offset.Value} in the Topic {result.Topic}");
            }
        }
    }
}