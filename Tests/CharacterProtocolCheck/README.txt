CHARACTER CREATION PROTOCOL REGRESSION CHECK

Requires the .NET 10 SDK. This console check loads serializer DLLs read-only.
It does not connect to a game server or read/write any player database.

From this test folder, run:
  dotnet run -- "<built-server-folder>" check "ClientOrder-r800683.json"

The fixture was generated independently from the known client-ordered r800683
schema before the server fix. It covers both genders, all seven schools,
names, account/character IDs and appearance. An alphabetically ordered
serializer fails this exact fixture with EndOfStreamException. The fixed
serializer preserves those fields correctly in all fourteen cases.

Use fixtures for the matching client revision. Creating fixture bytes and
reading them with the same broken serializer would hide this regression.
The write mode is for generating additional reference fixtures from a known
correct schema, not for replacing independent expected data during testing.
