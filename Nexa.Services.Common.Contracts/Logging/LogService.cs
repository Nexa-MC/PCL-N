



namespace Nexa.Services.Logging;

public interface ILogOperationSink
{
    void OnOperation(string subsystem, TimeSpan duration, bool succeeded);
}
