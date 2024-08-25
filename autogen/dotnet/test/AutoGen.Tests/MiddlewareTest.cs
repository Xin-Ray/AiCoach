﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// MiddlewareTest.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using FluentAssertions;
using Xunit;

namespace AutoGen.Tests;

public partial class MiddlewareTest
{
    [Function]
    public async Task<string> Echo(string message)
    {
        return $"[FUNC] {message}";
    }

    [Fact]
    public async Task HumanInputMiddlewareTestAsync()
    {
        var agent = new EchoAgent("echo");
        var neverAskUserInputMW = new HumanInputMiddleware(mode: HumanInputMode.NEVER);

        var neverInputAgent = agent.RegisterMiddleware(neverAskUserInputMW);
        var reply = await neverInputAgent.SendAsync("hello");
        reply.GetContent()!.Should().Be("hello");
        reply.From.Should().Be("echo");

        var alwaysAskUserInputMW = new HumanInputMiddleware(
            mode: HumanInputMode.ALWAYS,
            getInput: () => "input");

        var alwaysInputAgent = agent.RegisterMiddleware(alwaysAskUserInputMW);
        reply = await alwaysInputAgent.SendAsync("hello");
        reply.GetContent()!.Should().Be("input");
        reply.From.Should().Be("echo");

        // test auto mode
        // if the reply from echo is not terminate message, return the original reply
        var autoAskUserInputMW = new HumanInputMiddleware(
            mode: HumanInputMode.AUTO,
            isTermination: async (messages, ct) => messages.Last()?.GetContent() == "terminate",
            getInput: () => "input",
            exitKeyword: "exit");
        var autoInputAgent = agent.RegisterMiddleware(autoAskUserInputMW);
        reply = await autoInputAgent.SendAsync("hello");
        reply.GetContent()!.Should().Be("hello");

        // if the reply from echo is terminate message, asking user for input
        reply = await autoInputAgent.SendAsync("terminate");
        reply.GetContent()!.Should().Be("input");

        // if the reply from echo is terminate message, and user input is exit, return the TERMINATE message
        autoAskUserInputMW = new HumanInputMiddleware(
            mode: HumanInputMode.AUTO,
            isTermination: async (messages, ct) => messages.Last().GetContent() == "terminate",
            getInput: () => "exit",
            exitKeyword: "exit");
        autoInputAgent = agent.RegisterMiddleware(autoAskUserInputMW);

        reply = await autoInputAgent.SendAsync("terminate");
        reply.IsGroupChatTerminateMessage().Should().BeTrue();
    }

    [Fact]
    public async Task FunctionCallMiddlewareTestAsync()
    {
        var agent = new EchoAgent("echo");
        var args = new EchoSchema { message = "hello" };
        var argsJson = JsonSerializer.Serialize(args) ?? throw new InvalidOperationException("Failed to serialize args");
        var functionCall = new FunctionCall("echo", argsJson);
        var functionCallAgent = agent.RegisterMiddleware(async (messages, options, agent, ct) =>
        {
            if (options?.Functions is null)
            {
                return await agent.GenerateReplyAsync(messages, options, ct);
            }

            return new ToolCallMessage(functionCall.Name, functionCall.Arguments, from: agent.Name);
        });

        // test 1
        // middleware should invoke function call if the message is a function call message
        var mw = new FunctionCallMiddleware(
            functionMap: new Dictionary<string, Func<string, Task<string>>> { { "echo", EchoWrapper } });

        var testAgent = agent.RegisterMiddleware(mw);
        var functionCallMessage = new ToolCallMessage(functionCall.Name, functionCall.Arguments, from: "user");
        var reply = await testAgent.SendAsync(functionCallMessage);
        reply.Should().BeOfType<ToolCallResultMessage>();
        reply.GetContent()!.Should().Be("[FUNC] hello");
        reply.From.Should().Be("echo");

        // test 2
        // middleware should invoke function call if agent reply is a function call message
        mw = new FunctionCallMiddleware(
            functions: [this.EchoFunctionContract],
            functionMap: new Dictionary<string, Func<string, Task<string>>> { { "echo", EchoWrapper } });
        testAgent = functionCallAgent.RegisterMiddleware(mw);
        reply = await testAgent.SendAsync("hello");
        reply.GetContent()!.Should().Be("[FUNC] hello");
        reply.From.Should().Be("echo");

        // test 3
        // middleware should return original reply if the reply from agent is not a function call message
        mw = new FunctionCallMiddleware(
            functionMap: new Dictionary<string, Func<string, Task<string>>> { { "echo", EchoWrapper } });
        testAgent = agent.RegisterMiddleware(mw);
        reply = await testAgent.SendAsync("hello");
        reply.GetContent()!.Should().Be("hello");
        reply.From.Should().Be("echo");

        // test 4
        // middleware should return an error message if the function name is not available when invoking the function from previous agent reply
        mw = new FunctionCallMiddleware(
            functionMap: new Dictionary<string, Func<string, Task<string>>> { { "echo2", EchoWrapper } });
        testAgent = agent.RegisterMiddleware(mw);
        reply = await testAgent.SendAsync(functionCallMessage);
        reply.GetContent()!.Should().Be("Function echo is not available. Available functions are: echo2");
    }
}
