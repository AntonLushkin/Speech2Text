using System;
using System.Collections.Generic;

namespace SpeechToText.Core
{
    public sealed class WorkflowStateMachine
    {
        private readonly object _sync = new object();
        private WorkflowState _state = WorkflowState.Idle;

        private static readonly Dictionary<WorkflowState, HashSet<WorkflowState>> Allowed =
            new Dictionary<WorkflowState, HashSet<WorkflowState>>
            {
                [WorkflowState.Idle] = new HashSet<WorkflowState>
                {
                    WorkflowState.Recording,
                    WorkflowState.Transcribing
                },
                [WorkflowState.Recording] = new HashSet<WorkflowState>
                {
                    WorkflowState.Transcribing,
                    WorkflowState.Cancelled,
                    WorkflowState.Error
                },
                [WorkflowState.Transcribing] = new HashSet<WorkflowState>
                {
                    WorkflowState.Editing,
                    WorkflowState.Inserting,
                    WorkflowState.Cancelled,
                    WorkflowState.Error
                },
                [WorkflowState.Editing] = new HashSet<WorkflowState>
                {
                    WorkflowState.Inserting,
                    WorkflowState.Cancelled,
                    WorkflowState.Error
                },
                [WorkflowState.Inserting] = new HashSet<WorkflowState>
                {
                    WorkflowState.Completed,
                    WorkflowState.Error
                },
                [WorkflowState.Completed] = new HashSet<WorkflowState>
                {
                    WorkflowState.Idle
                },
                [WorkflowState.Error] = new HashSet<WorkflowState>
                {
                    WorkflowState.Idle
                },
                [WorkflowState.Cancelled] = new HashSet<WorkflowState>
                {
                    WorkflowState.Idle
                }
            };

        public event EventHandler<WorkflowState> StateChanged;

        public WorkflowState State
        {
            get
            {
                lock (_sync)
                {
                    return _state;
                }
            }
        }

        public bool TryTransition(WorkflowState next)
        {
            EventHandler<WorkflowState> handler;
            lock (_sync)
            {
                if (!Allowed[_state].Contains(next))
                {
                    return false;
                }

                _state = next;
                handler = StateChanged;
            }

            handler?.Invoke(this, next);
            return true;
        }

        public void Transition(WorkflowState next)
        {
            if (!TryTransition(next))
            {
                throw new InvalidOperationException(
                    $"Недопустимый переход состояния: {State} → {next}.");
            }
        }
    }
}
