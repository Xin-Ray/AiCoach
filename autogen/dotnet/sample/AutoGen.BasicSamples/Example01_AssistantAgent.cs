﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Example01_AssistantAgent.cs

using AutoGen;
using AutoGen.BasicSample;
using AutoGen.Core;
using FluentAssertions;

/// <summary>
/// This example shows the basic usage of <see cref="ConversableAgent"/> class.
/// </summary>
public static class Example01_AssistantAgent
{
    public static async Task RunAsync()
    {
        var gpt35 = LLMConfiguration.GetAzureOpenAIGPT3_5_Turbo();
        var config = new ConversableAgentConfig
        {
            Temperature = 0,
            ConfigList = [gpt35],
        };

        // create assistant agent
        var assistantAgent = new AssistantAgent(
            name: "assistant",
            systemMessage: "You convert what user said to all uppercase.",
            llmConfig: config)
            .RegisterPrintMessage();

        // talk to the assistant agent
        var reply = await assistantAgent.SendAsync("hello world");
        reply.Should().BeOfType<TextMessage>();
        reply.GetContent().Should().Be("HELLO WORLD");

        // to carry on the conversation, pass the previous conversation history to the next call
        var conversationHistory = new List<IMessage>
        {
            new TextMessage(Role.User, "hello world"), // first message
            reply, // reply from assistant agent
        };

        reply = await assistantAgent.SendAsync("hello world again", conversationHistory);
        reply.Should().BeOfType<TextMessage>();
        reply.GetContent().Should().Be("HELLO WORLD AGAIN");
    }
}
