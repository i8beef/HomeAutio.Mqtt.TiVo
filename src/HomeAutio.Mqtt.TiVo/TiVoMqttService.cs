using System.Threading;
using System.Threading.Tasks;
using HomeAutio.Mqtt.Core;
using I8Beef.TiVo;
using I8Beef.TiVo.Commands;
using I8Beef.TiVo.Events;
using I8Beef.TiVo.Responses;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace HomeAutio.Mqtt.TiVo
{
    /// <summary>
    /// TiVo MQTT Service.
    /// </summary>
    public class TiVoMqttService : ServiceBase
    {
        private ILogger<TiVoMqttService> _log;
        private bool _disposed = false;

        private Client _client;
        private string _tivoName;

        /// <summary>
        /// Initializes a new instance of the <see cref="TiVoMqttService"/> class.
        /// </summary>
        /// <param name="logger">Logging instance.</param>
        /// <param name="tivoClient">TiVo client.</param>
        /// <param name="tivoName">TiVo name.</param>
        /// <param name="brokerSettings">MQTT broker settings.</param>
        public TiVoMqttService(
            ILogger<TiVoMqttService> logger,
            Client tivoClient,
            string tivoName,
            BrokerSettings brokerSettings)
            : base(logger, brokerSettings, "tivo/" + tivoName)
        {
            _log = logger;
            SubscribedTopics.Add(TopicRoot + "/controls/+/set");

            _client = tivoClient;
            _tivoName = tivoName;

            _client.EventReceived += TiVo_EventReceived;

            // TiVo client logging
            _client.MessageSent += (object sender, MessageSentEventArgs e) => { _log.LogInformation("TiVo Message sent: " + e.Message); };
            _client.MessageReceived += (object sender, MessageReceivedEventArgs e) => { _log.LogInformation("TiVo Message received: " + e.Message); };
            _client.Error += (object sender, System.IO.ErrorEventArgs e) =>
            {
                var exception = e.GetException();
                _log.LogError(exception, exception.Message);
                System.Environment.Exit(1);
            };
        }

        #region Service implementation

        /// <inheritdoc />
        protected override Task StartServiceAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            _client.Connect();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override Task StopServiceAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.CompletedTask;
        }

        #endregion

        #region MQTT Implementation

        /// <summary>
        /// Handles commands for the TiVo published to MQTT.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        protected override async void Mqtt_MqttMsgPublishReceived(object sender, MqttApplicationMessageReceivedEventArgs e)
        {
            var message = e.ApplicationMessage.ConvertPayloadToString();
            _log.LogInformation("MQTT message received for topic " + e.ApplicationMessage.Topic + ": " + message);

            var commandType = e.ApplicationMessage.Topic.Replace(TopicRoot + "/controls/", string.Empty).Replace("/set", string.Empty);

            Command command = null;
            if (commandType == "setCh")
            {
                var messageParts = message.Split('.');
                if (int.TryParse(messageParts[0], out int channel))
                {
                    if (messageParts.Length == 1)
                    {
                        command = new SetChCommand { Channel = channel };
                    }
                    else if (messageParts.Length == 2 && int.TryParse(messageParts[0], out int subchannel))
                    {
                        command = new SetChCommand { Channel = channel, Subchannel = subchannel };
                    }
                }
            }

            if (commandType == "forceCh")
            {
                var messageParts = message.Split('.');
                if (int.TryParse(messageParts[0], out int channel))
                {
                    if (messageParts.Length == 1)
                    {
                        command = new ForceChCommand { Channel = channel };
                    }
                    else if (messageParts.Length == 2 && int.TryParse(messageParts[0], out int subchannel))
                    {
                        command = new ForceChCommand { Channel = channel, Subchannel = subchannel };
                    }
                }
            }

            if (commandType == "irCode")
                command = new IrCommand { IrCode = message };

            if (commandType == "teleport")
                command = new TeleportCommand { TeleportCode = message };

            if (commandType == "keyboard")
                command = new KeyboardCommand { KeyboardCode = message };

            if (command != null)
            {
                await _client.SendCommandAsync(command)
                    .ConfigureAwait(false);
            }
        }

        #endregion

        #region TiVo implementation

        /// <summary>
        /// Handles publishing updates to the TiVo current activity to MQTT.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void TiVo_EventReceived(object sender, ResponseEventArgs e)
        {
            _log.LogInformation($"TiVo event received: {e.Response.GetType()} {e.Response.Code} {e.Response.Value}");

            if (e.Response.GetType().Name == "ChannelStatusResponse")
            {
                var channelStatusResponse = e.Response as ChannelStatusResponse;
                await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic(TopicRoot + "/currentChannel")
                        .WithPayload(channelStatusResponse.Channel + (channelStatusResponse.Subchannel.HasValue ? $".{channelStatusResponse.Subchannel.Value}" : string.Empty))
                        .WithAtLeastOnceQoS()
                        .WithRetainFlag()
                        .Build())
                        .ConfigureAwait(false);
            }
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Dispose implementation.
        /// </summary>
        /// <param name="disposing">Indicates if disposing.</param>
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_client != null)
                {
                    _client.Dispose();
                }
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        #endregion
    }
}
