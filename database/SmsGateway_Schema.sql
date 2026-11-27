IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE TABLE [ApiClients] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        [KeyHash] nvarchar(64) NOT NULL,
        [KeyPrefix] nvarchar(16) NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastUsedAt] datetime2 NULL,
        CONSTRAINT [PK_ApiClients] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE TABLE [Devices] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        [PhoneNumber] nvarchar(20) NULL,
        [KeyHash] nvarchar(64) NOT NULL,
        [KeyPrefix] nvarchar(16) NOT NULL,
        [IsActive] bit NOT NULL,
        [MaxPerMinute] int NOT NULL,
        [DailyLimit] int NOT NULL,
        [SentToday] int NOT NULL,
        [SentTodayDate] datetime2 NOT NULL,
        [LastSeenAt] datetime2 NULL,
        [AppVersion] nvarchar(32) NULL,
        [BatteryLevel] int NULL,
        [SimInfo] nvarchar(200) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Devices] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE TABLE [SmsMessages] (
        [Id] uniqueidentifier NOT NULL,
        [ApiClientId] int NOT NULL,
        [ToNumber] nvarchar(20) NOT NULL,
        [Body] nvarchar(1600) NOT NULL,
        [Status] int NOT NULL,
        [Priority] int NOT NULL,
        [Attempts] int NOT NULL,
        [MaxAttempts] int NOT NULL,
        [DeviceId] int NULL,
        [PreferredDeviceId] int NULL,
        [ClientReference] nvarchar(100) NULL,
        [CallbackUrl] nvarchar(500) NULL,
        [ErrorMessage] nvarchar(500) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [AssignedAt] datetime2 NULL,
        [SentAt] datetime2 NULL,
        [DeliveredAt] datetime2 NULL,
        [FailedAt] datetime2 NULL,
        [NextRetryAt] datetime2 NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_SmsMessages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SmsMessages_ApiClients_ApiClientId] FOREIGN KEY ([ApiClientId]) REFERENCES [ApiClients] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SmsMessages_Devices_DeviceId] FOREIGN KEY ([DeviceId]) REFERENCES [Devices] ([Id]) ON DELETE SET NULL
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE TABLE [SmsEvents] (
        [Id] bigint NOT NULL IDENTITY,
        [SmsMessageId] uniqueidentifier NOT NULL,
        [DeviceId] int NULL,
        [Event] nvarchar(40) NOT NULL,
        [Details] nvarchar(1000) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_SmsEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SmsEvents_SmsMessages_SmsMessageId] FOREIGN KEY ([SmsMessageId]) REFERENCES [SmsMessages] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApiClients_KeyHash] ON [ApiClients] ([KeyHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Devices_KeyHash] ON [Devices] ([KeyHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SmsEvents_SmsMessageId] ON [SmsEvents] ([SmsMessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_SmsMessages_ApiClientId_ClientReference] ON [SmsMessages] ([ApiClientId], [ClientReference]) WHERE [ClientReference] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SmsMessages_CreatedAt] ON [SmsMessages] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SmsMessages_DeviceId_AssignedAt] ON [SmsMessages] ([DeviceId], [AssignedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SmsMessages_Status_Priority_CreatedAt] ON [SmsMessages] ([Status], [Priority], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111093140_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111093140_InitialCreate', N'9.0.23');
END;

COMMIT;
GO

