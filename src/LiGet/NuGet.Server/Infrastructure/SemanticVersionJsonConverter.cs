// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 

using System;
using Newtonsoft.Json;
using NuGet;
using NuGet.Versioning;

namespace LiGet.NuGet.Server.Infrastructure
{
    public class SemanticVersionJsonConverter
        : JsonConverter
    {
        private readonly JsonSerializer _serializer = new JsonSerializer();

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(SemanticVersion)
                || objectType == typeof(Version);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var value = _serializer.Deserialize<string>(reader);

            if (!string.IsNullOrEmpty(value))
            {
                if (objectType == typeof(SemanticVersion))
                {
                    return SemanticVersion.Parse(value);
                }

                if (objectType == typeof(Version))
                {
                    return Version.Parse(value);
                }

                if (objectType == typeof(NuGetVersion))
                {
                    return NuGetVersion.Parse(value);
                }
            }

            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var semanticVersion = value as SemanticVersion;
            if (semanticVersion != null)
            {
                _serializer.Serialize(writer, semanticVersion.ToFullString());
            }
            else
            {
                var nversion = value as NuGetVersion;
                if(nversion != null)
                {
                    _serializer.Serialize(writer, nversion.OriginalVersion);
                }
                else {
                    _serializer.Serialize(writer, value.ToString());
                }
            }
        }
    }
}