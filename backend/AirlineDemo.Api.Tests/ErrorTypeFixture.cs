// The error.type allowlist rule only accepts lowercase dotted names, so the fixture exception lives in a lowercase namespace.
namespace airline.demo
{
    public sealed class boom(string message) : System.Exception(message);
}
