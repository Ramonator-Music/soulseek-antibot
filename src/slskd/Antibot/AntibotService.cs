// <copyright file="AntibotService.cs" company="Ramonator">
//   ┍━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ ━━━━ ━  ━┉   ┉     ┉
//   │ Copyright (c) 2026 Ramonator.
//   │
//   │ This program is free software: you can redistribute it and/or modify
//   │ it under the terms of the GNU Affero General Public License as published
//   │ by the Free Software Foundation, version 3.
//   │
//   │ This program is distributed in the hope that it will be useful,
//   │ but WITHOUT ANY WARRANTY; without even the implied warranty of
//   │ MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//   │ GNU Affero General Public License for more details.
//   │
//   │ You should have received a copy of the GNU Affero General Public License
//   │ along with this program.  If not, see https://www.gnu.org/licenses/.
//   │
//   │ This program is distributed with Additional Terms pursuant to Section 7
//   │ of the AGPLv3.  See the LICENSE file in the root directory of this
//   │ project for the complete terms and conditions.
//   │
//   ├╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌ ╌ ╌╌╌╌ ╌
//   │ SPDX-FileCopyrightText: Ramonator
//   │ SPDX-License-Identifier: AGPL-3.0-only
//   ╰───────────────────────────────────────────╶──── ─ ─── ─  ── ──┈  ┈
// </copyright>

using Microsoft.Extensions.Options;

namespace slskd.Antibot
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Serilog;
    using slskd.Transfers.Uploads;
    using slskd.Validation;
    using Soulseek;

    /// <summary>
    ///     Upload request gate for the modified version: a per-file download cap and a simple human
    ///     verification over private chat.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The challenge word is derived from the username with an HMAC keyed by a secret stored in the data
    ///         directory, so it is stable across restarts and no pending-challenge state needs to be persisted.
    ///     </para>
    ///     <para>
    ///         Verified usernames are persisted to <c>antibot_verified.json</c> in the data directory.
    ///     </para>
    /// </remarks>
    public class AntibotService
    {
        private static readonly string[] Words =
        [
            "mango", "tigre", "piano", "cometa", "lluvia", "volcan", "girasol", "ballena", "canela", "tambor",
            "faro", "brujula", "cactus", "delfin", "eclipse", "fresa", "glaciar", "hormiga", "iguana", "jaguar",
            "koala", "limon", "medusa", "nogal", "oregano", "pantera", "quinoa", "rayo", "sandia", "tortuga",
            "uva", "violin", "yuca", "zorro", "acordeon", "bambu", "cereza", "dragon", "estrella", "flauta",
            "guitarra", "helado", "isla", "jirafa", "laguna", "maraca", "naranja", "oruga", "paloma", "queso",
            "rana", "selva", "trompeta", "urraca", "vela", "abeja", "bosque", "cangrejo", "durazno", "esponja",
            "farol", "gaviota", "hongo", "jazmin", "lechuza", "manzana", "nutria", "ostra", "pepino", "remolino",
            "salmon", "tucan", "vaquero", "almendra", "buho", "colibri", "duende", "espiga", "foca", "granizo",
            "hamaca", "incienso", "jengibre", "lobo", "molino", "nectar", "oliva", "pinguino", "roble", "sirena",
            "trueno", "vainilla", "ardilla", "bufanda", "caracol", "domino", "enano", "fogata", "grillo", "hielo",
            "jabali", "lagarto", "mariposa", "niebla", "oveja", "pirata", "rubi", "semilla", "tiburon", "velero",
            "arcoiris", "banjo", "cascada", "desierto", "escoba", "fantasma", "gorila", "hechizo", "linterna", "marimba",
        ];

        public AntibotService(IOptionsMonitor<global::slskd.Options> optionsMonitor, ISoulseekClient soulseekClient, IUploadService uploadService)
        {
            OptionsMonitor = optionsMonitor;
            Client = soulseekClient;
            Uploads = uploadService;

            VerifiedFile = Path.Combine(Program.DataDirectory, "antibot_verified.json");
            Key = LoadOrCreateKey(Path.Combine(Program.DataDirectory, "antibot.key"));
            Verified = LoadVerified(VerifiedFile);

            Log.Information("Antibot gate loaded with {Count} verified user(s)", Verified.Count);
        }

        private ILogger Log { get; } = Serilog.Log.ForContext<AntibotService>();
        private IOptionsMonitor<global::slskd.Options> OptionsMonitor { get; }
        private ISoulseekClient Client { get; }
        private IUploadService Uploads { get; }
        private string VerifiedFile { get; }
        private byte[] Key { get; }
        private HashSet<string> Verified { get; }
        private object VerifiedLock { get; } = new object();
        private ConcurrentDictionary<string, DateTime> LastChallenge { get; } = new ConcurrentDictionary<string, DateTime>();

        /// <summary>
        ///     Decides whether an incoming upload request should be rejected.
        /// </summary>
        /// <param name="username">The requesting user.</param>
        /// <param name="filename">The requested file, as sent by the remote client.</param>
        /// <returns>The rejection reason to send to the remote client, or null to let the request through.</returns>
        public string CheckEnqueue(string username, string filename)
        {
            var options = OptionsMonitor.CurrentValue.Antibot;

            if (!options.Enabled || IsExempt(username, options))
            {
                return null;
            }

            if (options.MaxDownloadsPerFile.HasValue)
            {
                var succeeded = TransferStates.Completed | TransferStates.Succeeded;

                // includes removed records: the upload history is cleared daily, but the rows stay in the database
                var count = Uploads.List(t => t.Username == username && t.Filename == filename && t.State == succeeded, includeRemoved: true).Count;

                if (count >= options.MaxDownloadsPerFile.Value)
                {
                    Log.Information("Rejected enqueue request for user {Username}: per-file limit ({Count}/{Max}) for {Filename}", username, count, options.MaxDownloadsPerFile.Value, filename);
                    return $"Already downloaded this file {options.MaxDownloadsPerFile.Value} times";
                }
            }

            if (options.Verification.Enabled && !IsVerified(username))
            {
                Log.Information("Rejected enqueue request for user {Username}: verification required", username);
                SendChallenge(username, options.Verification);
                return "Verification required: check private messages";
            }

            return null;
        }

        /// <summary>
        ///     Checks an incoming private message for a correct verification answer.
        /// </summary>
        /// <param name="username">The sender.</param>
        /// <param name="id">The message id, used to acknowledge the message to the server.</param>
        /// <param name="message">The message text.</param>
        /// <returns>True if the message was a correct answer and was consumed; false to handle it normally.</returns>
        public bool TryHandlePrivateMessage(string username, int id, string message)
        {
            var options = OptionsMonitor.CurrentValue.Antibot;

            if (!options.Enabled || !options.Verification.Enabled || IsExempt(username, options) || IsVerified(username))
            {
                return false;
            }

            var word = WordFor(username);

            if (!Tokenize(message).Contains(word))
            {
                return false;
            }

            lock (VerifiedLock)
            {
                Verified.Add(username);
                SaveVerified();
            }

            LastChallenge.TryRemove(username, out _);
            Log.Information("Antibot: user {Username} verified", username);

            _ = Task.Run(async () =>
            {
                try
                {
                    // consumed messages never reach the conversation service, which is what normally acknowledges them;
                    // without this the server would replay the message on every login
                    await Client.AcknowledgePrivateMessageAsync(id);
                    await SendLinesAsync(username, Render(options.Verification.SuccessMessage, word, options.Verification.SourceUrl));
                }
                catch (Exception ex)
                {
                    Log.Warning("Antibot: failed to acknowledge or confirm verification for {Username}: {Message}", username, ex.Message);
                }
            });

            return true;
        }

        private static bool IsExempt(string username, AntibotOptions options)
            => options.Exempt.Contains(username, StringComparer.Ordinal);

        private static IEnumerable<string> Tokenize(string text)
        {
            var normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);

                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Render(string template, string word, string sourceUrl)
            => (template ?? string.Empty)
                .Replace("{word}", word, StringComparison.Ordinal)
                .Replace("{source}", string.IsNullOrWhiteSpace(sourceUrl) ? "-" : sourceUrl, StringComparison.Ordinal);

        private static byte[] LoadOrCreateKey(string file)
        {
            if (System.IO.File.Exists(file))
            {
                return System.IO.File.ReadAllBytes(file);
            }

            var key = RandomNumberGenerator.GetBytes(32);
            System.IO.File.WriteAllBytes(file, key);
            return key;
        }

        private static HashSet<string> LoadVerified(string file)
        {
            if (!System.IO.File.Exists(file))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var users = JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(file)) ?? [];
            return new HashSet<string>(users, StringComparer.Ordinal);
        }

        private bool IsVerified(string username)
        {
            lock (VerifiedLock)
            {
                return Verified.Contains(username);
            }
        }

        private void SaveVerified()
        {
            // caller holds VerifiedLock
            var temp = VerifiedFile + ".tmp";
            var json = JsonSerializer.Serialize(Verified.OrderBy(u => u, StringComparer.Ordinal).ToArray(), new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(temp, json);
            System.IO.File.Move(temp, VerifiedFile, overwrite: true);
        }

        private async Task SendLinesAsync(string username, string text)
        {
            // the server silently drops private messages that contain line breaks, so each line goes as its own message
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await Client.SendPrivateMessageAsync(username, line);
            }
        }

        private string WordFor(string username)
        {
            var hash = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(username));
            var index = BitConverter.ToUInt32(hash, 0) % (uint)Words.Length;
            return Words[index];
        }

        private void SendChallenge(string username, AntibotOptions.VerificationOptions options)
        {
            var now = DateTime.UtcNow;
            var cooldown = TimeSpan.FromMinutes(options.CooldownMinutes);

            if (LastChallenge.TryGetValue(username, out var last) && now - last < cooldown)
            {
                return;
            }

            LastChallenge[username] = now;

            if (LastChallenge.Count > 10_000)
            {
                foreach (var stale in LastChallenge.Where(kvp => now - kvp.Value >= cooldown).Select(kvp => kvp.Key).ToList())
                {
                    LastChallenge.TryRemove(stale, out _);
                }
            }

            var text = Render(options.ChallengeMessage, WordFor(username), options.SourceUrl);

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendLinesAsync(username, text);
                    Log.Information("Antibot: sent verification challenge to {Username}", username);
                }
                catch (Exception ex)
                {
                    Log.Warning("Antibot: failed to send verification challenge to {Username}: {Message}", username, ex.Message);
                }
            });
        }
    }

    /// <summary>
    ///     Options for the upload request gate of the modified version.
    /// </summary>
    public class AntibotOptions
    {
        /// <summary>
        ///     Gets a value indicating whether the gate is enabled.
        /// </summary>
        public bool Enabled { get; init; } = false;

        /// <summary>
        ///     Gets the maximum number of successful downloads of the same file by the same user. Null disables the cap.
        /// </summary>
        [Range(1, int.MaxValue)]
        public int? MaxDownloadsPerFile { get; init; } = null;

        /// <summary>
        ///     Gets usernames that bypass both the cap and the verification.
        /// </summary>
        public string[] Exempt { get; init; } = [];

        /// <summary>
        ///     Gets human verification options.
        /// </summary>
        [Validate]
        public VerificationOptions Verification { get; init; } = new VerificationOptions();

        /// <summary>
        ///     Human verification options.
        /// </summary>
        public class VerificationOptions
        {
            /// <summary>
            ///     Gets a value indicating whether users must verify before downloading.
            /// </summary>
            public bool Enabled { get; init; } = false;

            /// <summary>
            ///     Gets the minimum number of minutes between challenge messages to the same user.
            /// </summary>
            [Range(1, 1440)]
            public int CooldownMinutes { get; init; } = 10;

            /// <summary>
            ///     Gets the URL of the source code of this modified version (AGPLv3 section 13). Rendered as {source}.
            /// </summary>
            public string SourceUrl { get; init; } = null;

            /// <summary>
            ///     Gets the challenge message. {word} is replaced with the user's word and {source} with the source URL.
            /// </summary>
            public string ChallengeMessage { get; init; } =
                "Hola 👋 Comparto solo con personas, no con bots. Para descargar, escribe aquí la palabra «{word}» (una sola vez).\n" +
                "Hi 👋 I only share with humans, not bots. To download, type the word «{word}» here (just once).\n" +
                "Código de este cliente modificado / Source of this modified client: {source}";

            /// <summary>
            ///     Gets the message sent after a correct answer.
            /// </summary>
            public string SuccessMessage { get; init; } =
                "¡Listo, quedaste verificado! Vuelve a intentar la descarga.\n" +
                "Done, you're verified! Please retry your download.";
        }
    }
}
