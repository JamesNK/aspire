// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Azure.Tests;

internal sealed class RemoteTestOutputHelper : ITestOutputHelper
{
    public string Output => string.Empty;

    public void Write(string message) => Console.Write(message);

    public void Write(string format, params object[] args) => Console.Write(format, args);

    public void WriteLine(string message) => Console.WriteLine(message);

    public void WriteLine(string format, params object[] args) => Console.WriteLine(format, args);

    public static RemoteInvokeOptions CreateRemoteInvokeOptions()
    {
        var options = new RemoteInvokeOptions { Start = false };
        options.StartInfo.RedirectStandardError = true;
        options.StartInfo.RedirectStandardOutput = true;
        return options;
    }

    public static void Start(RemoteInvokeHandle handle, ITestOutputHelper testOutputHelper)
    {
        handle.Process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                testOutputHelper.WriteLine($"[RemoteExecutor] {eventArgs.Data}");
            }
        };
        handle.Process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                testOutputHelper.WriteLine($"[RemoteExecutor] ERROR: {eventArgs.Data}");
            }
        };

        handle.Process.Start();
        handle.Process.BeginErrorReadLine();
        handle.Process.BeginOutputReadLine();
    }
}