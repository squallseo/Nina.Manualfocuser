using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RTG.ManualFocuser.Util
{
    public class Integration : ISubscriber, IDisposable
    {
        private IMessageBroker messageBroker;

        public Integration(IMessageBroker messageBroker)
        {
            this.messageBroker = messageBroker;

            this.messageBroker.Subscribe("RTG.ManualFocuser.RegisterFocuser", this);
            this.messageBroker.Subscribe("RTG.ManualFocuser.GotoFocus", this);
        }

        public void Dispose()
        {
            this.messageBroker.Unsubscribe("RTG.ManualFocuser.RegisterFocuser", this);
            this.messageBroker.Unsubscribe("RTG.ManualFocuser.GotoFocus", this);
        }

        public async Task OnMessageReceived(IMessage message)
        {
            if (message.Topic == "RTG.ManualFocuser.RegisterFocuser")
            {
                RTG.ManualFocuser.ManualFocuser.AddLensConfigIfNecessary((string)message.Content);
            }
            else if (message.Topic == "RTG.ManualFocuser.GotoFocus")
            {
                if (RTG.ManualFocuser.ManualFocuser.Focuser.GetInfo().Connected) {
                    RTG.ManualFocuser.ManualFocuser.AddLensConfigIfNecessary(RTG.ManualFocuser.ManualFocuser.Focuser.GetInfo().DisplayName);
                    await RTG.ManualFocuser.ManualFocuser.Focuser.MoveFocuser(RTG.ManualFocuser.ManualFocuser.GetFocusPosition(RTG.ManualFocuser.ManualFocuser.Focuser.GetInfo().DisplayName), new());
                }
            }
        }
    }
}
