namespace Mainframe.Client;

public sealed class KernelRpcException : Exception
{
    public string Code
    {
        get;
    }
    public string Outcome
    {
        get;
    }

    public KernelRpcException(string code, string message, string outcome) : base(message)
    {
        Code = code;
        Outcome = outcome;
    }
}
