namespace slskd.Tests.Unit.Antibot
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Moq;
    using slskd.Antibot;
    using slskd.Transfers.Uploads;
    using Soulseek;
    using Xunit;

    public class AntibotServiceTests
    {
        private const int LinesPerChallenge = 4;

        [Fact]
        public async Task Burst_Of_New_Users_Messages_Only_Up_To_The_Limit()
        {
            var (antibot, client) = Create(maxPerMinute: 3);

            foreach (var username in new[] { "a", "b", "c", "d", "e" })
            {
                Assert.NotNull(antibot.CheckEnqueue(username, "file"));
            }

            await WaitForMessagesAsync(client, 3 * LinesPerChallenge);

            Assert.Equal(new[] { "a", "b", "c" }, Recipients(client));
        }

        [Fact]
        public async Task User_Over_The_Limit_Gets_The_Message_On_A_Later_Attempt()
        {
            var (antibot, client) = Create(maxPerMinute: 1);

            Assert.NotNull(antibot.CheckEnqueue("a", "file"));
            Assert.NotNull(antibot.CheckEnqueue("b", "file"));

            await WaitForMessagesAsync(client, LinesPerChallenge);
            Assert.Equal(new[] { "a" }, Recipients(client));

            await Task.Delay(TimeSpan.FromSeconds(61));

            Assert.NotNull(antibot.CheckEnqueue("b", "file"));

            await WaitForMessagesAsync(client, 2 * LinesPerChallenge);
            Assert.Equal(new[] { "a", "b" }, Recipients(client));
        }

        private static (AntibotService Antibot, Mock<ISoulseekClient> Client) Create(int maxPerMinute)
        {
            var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(directory);
            typeof(Program).GetProperty(nameof(Program.DataDirectory)).SetValue(null, directory);

            var options = new Options
            {
                Antibot = new AntibotOptions
                {
                    Enabled = true,
                    MaxDownloadsPerFile = null,
                    Verification = new AntibotOptions.VerificationOptions
                    {
                        Enabled = true,
                        CooldownMinutes = 1,
                        MaxChallengesPerMinute = maxPerMinute,
                    },
                },
            };

            var client = new Mock<ISoulseekClient>();
            client
                .Setup(m => m.SendPrivateMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken?>()))
                .Returns(Task.CompletedTask);

            return (new AntibotService(new TestOptionsMonitor<Options>(options), client.Object, Mock.Of<IUploadService>()), client);
        }

        private static string[] Recipients(Mock<ISoulseekClient> client)
            => client.Invocations
                .Where(i => i.Method.Name == nameof(ISoulseekClient.SendPrivateMessageAsync))
                .Select(i => (string)i.Arguments[0])
                .Distinct()
                .OrderBy(u => u)
                .ToArray();

        private static async Task WaitForMessagesAsync(Mock<ISoulseekClient> client, int expected)
        {
            for (var i = 0; i < 100 && client.Invocations.Count < expected; i++)
            {
                await Task.Delay(50);
            }

            // long enough for any message that should not have gone out to show up
            await Task.Delay(500);

            Assert.Equal(expected, client.Invocations.Count);
        }
    }
}
