﻿using AspNetCore.Authentication.Basic;

using BUTR.CrashReport.Server.Options;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Services;

public sealed class BasicUserValidationService : IBasicUserValidationService
{
    private readonly ILogger _logger;
    private readonly AuthOptions _options;

    public BasicUserValidationService(ILogger<BasicUserValidationService> logger, IOptionsSnapshot<AuthOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<bool> IsValidAsync(string username, string password) => Task.FromResult(string.Equals(_options.Username, username, StringComparison.Ordinal) &&
                                                                                        string.Equals(_options.Password, password, StringComparison.Ordinal));
}