/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * SPELL WORKSPACE RESOURCE OVERLAY
 * ========================================================================
 *
 * PURPOSE:
 * Supplies verified spell resources in memory from a staged build.
 *
 * USAGE EXAMPLE:
 * s_rootWad = SpellWorkspaceOverlay.Apply(ResourceWad());
 *
 * NOTE:
 * Original WAD files are opened for reading only.
 *
 * TODO:
 *
 * Created by: Spell Workspace contributors
 * Version: KALI 1.0
 * Last Updated: 10/13/2026
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Raven.Client.Documents.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Resources;

public sealed record SpellWorkspaceGrantRequest(ulong CharacterId, uint TemplateId, bool Treasure) : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 100;
}
public sealed record SpellWorkspaceGrantResult(bool Success, string Message);

internal static class SpellWorkspaceGrant {
    internal static object Characters() {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        var wizards = session.Query<Wizard>(collectionName: WizardCollection.CollectionName).Take(1000).ToList();
        return wizards.Select(wizard => new { wizard.CharId, Name = wizard.PlayerNameBehavior.GetWizardName(),
            KnownSpells = wizard.SpellbookBehavior.LearnedSpellTemplateIds.ToArray() }).ToArray();
    }
    internal static object Templates(IEnumerable<uint> ids) => ids.Select(id => {
        var spell = CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate;
        if (spell is null) { throw new InvalidOperationException("Missing spell template: " + id); }
        return new { TemplateId = id, InternalName = spell.m_name.ToString(), spell.m_accuracy,
            Pips = spell.m_spellRank.m_spellRank, Animation = spell.m_spellBase.ToString(), Image = spell.m_imageName.ToString() };
    }).ToArray();
    internal static async Task<SpellWorkspaceGrantResult> Grant(ActorSystem system, ulong characterId, uint templateId, bool treasure) {
        var template = CoreObjectFactory.GetCoreTemplate(templateId) as SpellTemplate;
        if (template is null || template.m_Treasure != treasure) { return new(false, "The requested spell type does not match this template."); }
        var online = OnlinePlayerCollection.GetOnlinePlayer(characterId);
        if (online is not null) {
            return await system.ActorSelection(online.ActorPath).Ask<SpellWorkspaceGrantResult>(
                new SpellWorkspaceGrantRequest(characterId, templateId, treasure), TimeSpan.FromSeconds(10));
        }
        var wizard = WizardCollection.GetCharacterUnloaded(characterId);
        if (wizard is null) { return new(false, "The selected wizard does not exist in this project database."); }
        if (treasure) { WizardCollection.AddTreasureCard(wizard, templateId); }
        else if (!wizard.SpellbookBehavior.LearnedSpellTemplateIds.Contains(templateId)) { WizardCollection.LearnSpell(wizard, templateId); }
        return new(true, "Spell saved. Log in to the selected wizard to use it.");
    }
}
