﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;
using Rebus.Activation;
using Rebus.Auditing.Sagas;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Persistence.SqlServer;
using Rebus.Sagas;
using Rebus.Serialization;
using Rebus.Transport.InMem;

namespace Rebus.Tests.Auditing
{
    [TestFixture]
    public class TestSagaAuditing : FixtureBase
    {
        const string TableName = "saga_snapshots";
        BuiltinHandlerActivator _handlerActivator;
        IBus _bus;

        protected override void SetUp()
        {
            SqlTestHelper.DropTable(TableName);

            _handlerActivator = Using(new BuiltinHandlerActivator());

            _bus = Configure.With(_handlerActivator)
                .Transport(t => t.UseInMemoryTransport(new InMemNetwork(), "saga-auditing"))
                .Options(e => e.EnableSagaAuditing().StoreInSqlServer(SqlTestHelper.ConnectionString, TableName))
                .Start();
        }

        [Test]
        public async Task ItWorks()
        {
            _handlerActivator.Register(() => new MySaga());

            await _bus.SendLocal("hej/med dig");
            await _bus.SendLocal("hej/med jer");
            await _bus.SendLocal("hej/igen");

            await Task.Delay(1000);

            var connectionProvider = new DbConnectionProvider(SqlTestHelper.ConnectionString);

            var storedCopies = new List<Tuple<Guid, int, ISagaData, Dictionary<string,string>>>();

            using (var connection = await connectionProvider.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"SELECT * FROM [{0}]", TableName);

                    using (var reader = command.ExecuteReader())
                    {
                        while (await reader.ReadAsync())
                        {
                            var id = (Guid)reader["id"];
                            var revision = (int)reader["revision"];
                            var sagaData = (ISagaData)new ObjectSerializer().Deserialize(Encoding.UTF8.GetBytes((string)reader["data"]));
                            var metadata = JsonConvert.DeserializeObject<Dictionary<string, string>>((string)reader["metadata"]);

                            var tuple = Tuple.Create(id, revision, sagaData, metadata);

                            storedCopies.Add(tuple);
                        }
                    }
                }

                await connection.Complete();
            }

            Assert.That(storedCopies.Count, Is.EqualTo(3));
            Assert.That(storedCopies.OrderBy(t => t.Item2).Select(t => t.Item2), Is.EqualTo(new[] {0, 1, 2}), "Expected three initial revisions");
        }

        class MySaga : Saga<MySagaData>, IAmInitiatedBy<string>
        {
            protected override void CorrelateMessages(ICorrelationConfig<MySagaData> config)
            {
                config.Correlate<string>(GetCorrelationId, d => d.CorrelationId);
            }

            static string GetCorrelationId(string s)
            {
                return s.Split('/').First();
            }

            public async Task Handle(string message)
            {
                if (Data.CorrelationId == null)
                {
                    Data.CorrelationId = GetCorrelationId(message);
                }

                Data.ReceivedMessages.Add(message);
            }
        }

        class MySagaData : ISagaData
        {
            public MySagaData()
            {
                ReceivedMessages = new HashSet<string>();
            }
            public Guid Id { get; set; }
            public int Revision { get; set; }
            public string CorrelationId { get; set; }
            public HashSet<string> ReceivedMessages { get; private set; }
        }
    }

}