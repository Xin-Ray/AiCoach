﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Example02_TwoAgent_MathChat.cs

using AutoGen;
using AutoGen.BasicSample;
using AutoGen.Core;
using FluentAssertions;
public static class Example02_TwoAgent_MathChat
{
    public static async Task RunAsync()
    {
        #region code_snippet_1
        // get gpt-3.5-turbo config
        var gpt35 = LLMConfiguration.GetAzureOpenAIGPT3_5_Turbo();

        // create teacher agent
        // teacher agent will create math questions
        var teacher = new AssistantAgent(
            name: "teacher",
            systemMessage: @"You are a teacher that create pre-school math question for student and check answer.
        If the answer is correct, you stop the conversation by saying [COMPLETE].
        If the answer is wrong, you ask student to fix it.",
            llmConfig: new ConversableAgentConfig
            {
                Temperature = 0,
                ConfigList = [gpt35],
            })
            .RegisterMiddleware(async (msgs, option, agent, _) =>
            {
                var reply = await agent.GenerateReplyAsync(msgs, option);
                if (reply.GetContent()?.ToLower().Contains("complete") is true)
                {
                    return new TextMessage(Role.Assistant, GroupChatExtension.TERMINATE, from: reply.From);
                }

                return reply;
            })
            .RegisterPrintMessage();

        // create student agent
        // student agent will answer the math questions
        var student = new AssistantAgent(
            name: "student",
            systemMessage: "You are a student that answer question from teacher",
            llmConfig: new ConversableAgentConfig
            {
                Temperature = 0,
                ConfigList = [gpt35],
            })
            .RegisterPrintMessage();

        // start the conversation
        var conversation = await student.InitiateChatAsync(
            receiver: teacher,
            message: "Hey teacher, please create math question for me.",
            maxRound: 10);

        // output
        // Message from teacher
        // --------------------
        // content: Of course!Here's a math question for you:
        // 
        // What is 2 + 3 ?
        // --------------------
        // 
        // Message from student
        // --------------------
        // content: The sum of 2 and 3 is 5.
        // --------------------
        // 
        // Message from teacher
        // --------------------
        // content: [GROUPCHAT_TERMINATE]
        // --------------------
        #endregion code_snippet_1

        conversation.Count().Should().BeLessThan(10);
        conversation.Last().IsGroupChatTerminateMessage().Should().BeTrue();
    }
}
