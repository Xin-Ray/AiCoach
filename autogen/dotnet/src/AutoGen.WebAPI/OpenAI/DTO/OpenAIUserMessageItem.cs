﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// OpenAIUserMessageItem.cs

using System.Text.Json.Serialization;

namespace AutoGen.WebAPI.OpenAI.DTO;

internal abstract class OpenAIUserMessageItem
{
    [JsonPropertyName("type")]
    public abstract string MessageType { get; }
}
