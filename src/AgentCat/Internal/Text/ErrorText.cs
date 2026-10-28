using System;

namespace AgentCat.Internal.Text;

internal static class ErrorText
{
    internal static string Of(Exception e)
    {
        string message;
        try
        {
            message = e.Message;
        }
        catch (Exception)
        {
            message = "";
        }

        return e.GetType().Name + ": " + message;
    }
}
