﻿namespace PaymentAutomation.Models;

public record EmailConfiguration
{
    public string Server { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
