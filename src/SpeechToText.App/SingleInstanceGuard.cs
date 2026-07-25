using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SpeechToText.App
{
    public sealed class SingleInstanceGuard : IDisposable
    {
        private const string MutexName =
            @"Local\SpeechToText.PersonalDictation.Mutex";
        private const string ActivateEventName =
            @"Local\SpeechToText.PersonalDictation.Activate";

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _activateEvent;
        private readonly bool _ownsMutex;
        private readonly CancellationTokenSource _cancellation =
            new CancellationTokenSource();

        private SingleInstanceGuard(
            Mutex mutex,
            EventWaitHandle activateEvent,
            bool ownsMutex)
        {
            _mutex = mutex;
            _activateEvent = activateEvent;
            _ownsMutex = ownsMutex;
        }

        public bool IsPrimary => _ownsMutex;

        public static SingleInstanceGuard Acquire()
        {
            bool createdNew;
            var mutex = new Mutex(true, MutexName, out createdNew);
            var activateEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ActivateEventName);

            var guard = new SingleInstanceGuard(
                mutex,
                activateEvent,
                createdNew);
            if (!createdNew)
            {
                activateEvent.Set();
            }

            return guard;
        }

        public void Listen(Dispatcher dispatcher, Action activated)
        {
            if (!_ownsMutex)
            {
                return;
            }

            Task.Run(() =>
            {
                var handles = new WaitHandle[]
                {
                    _activateEvent,
                    _cancellation.Token.WaitHandle
                };

                while (WaitHandle.WaitAny(handles) == 0)
                {
                    dispatcher.BeginInvoke(activated);
                }
            });
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
            _activateEvent.Dispose();
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
            _mutex.Dispose();
        }
    }
}
