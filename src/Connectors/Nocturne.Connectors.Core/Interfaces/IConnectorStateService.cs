using System;

namespace Nocturne.Connectors.Core.Interfaces
{
    public enum ConnectorState
    {
        Idle,
        Syncing,
        BackingOff,
        Error
    }

    public interface IConnectorStateService
    {
        ConnectorState CurrentState { get; }
        string? StateMessage { get; }
        void SetState(ConnectorState state, string? message = null);
    }
}
