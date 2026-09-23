using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>SubsonicStore and Crypto are process-wide singletons; tests touching them run serially.</summary>
[CollectionDefinition("StaticState", DisableParallelization = true)]
public class StaticStateCollection;
