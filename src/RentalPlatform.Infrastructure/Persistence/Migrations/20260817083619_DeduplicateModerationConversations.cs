using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeduplicateModerationConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ConversationsStore.GetOrCreateForModerationAsync used check-then-insert with no
            // unique constraint backing it, so two concurrent calls for the same member (two
            // moderators clicking "Message" together, a double-click, or — most likely — two
            // listing rejections for the same owner in quick succession via
            // ModerationNoteEmitter) could both insert a Moderation (Kind = 1) conversation for
            // that member. The unique filtered index created below cannot be built while such
            // duplicates exist, so this consolidates them first.
            //
            // For every member with more than one Moderation thread, the earliest-opened thread
            // (CreatedAt ascending, Id as a deterministic tie-break) is kept; every other
            // thread's messages and participant rows are re-pointed onto it — never deleted —
            // before the now-empty duplicate Conversation row is removed. This is chosen over
            // failing the migration on discovering duplicates because the race is confirmed
            // reachable in normal admin-console use (not merely theoretical), so the dev/staging
            // database plausibly already has some; a clean, reviewable, zero-message-loss
            // consolidation unblocks the fix immediately instead of requiring a manual DBA pass
            // first. If a member's two threads happen to share a participant (the same moderator
            // opened both — the only way this could happen is manually, since the store's own
            // idempotency check would normally prevent it even pre-fix), the duplicate
            // participant row is dropped rather than moved, since ConversationParticipants has
            // its own unique (ConversationId, UserId) index and keeping either copy of that
            // moderator's read cursor is equivalent.
            migrationBuilder.Sql(
                """
                IF OBJECT_ID('tempdb..#ModerationDup') IS NOT NULL DROP TABLE #ModerationDup;
                IF OBJECT_ID('tempdb..#ModerationMap') IS NOT NULL DROP TABLE #ModerationMap;

                -- Rank every Moderation conversation within its member (RenterId); rank 1 is the
                -- survivor.
                SELECT
                    c.Id,
                    c.RenterId,
                    ROW_NUMBER() OVER (PARTITION BY c.RenterId ORDER BY c.CreatedAt ASC, c.Id ASC) AS Rn
                INTO #ModerationDup
                FROM Conversations c
                WHERE c.Kind = 1;

                -- Loser -> keeper map: only members with more than one Moderation thread appear
                -- here.
                SELECT
                    loser.Id AS LoserId,
                    keeper.Id AS KeeperId
                INTO #ModerationMap
                FROM #ModerationDup loser
                JOIN #ModerationDup keeper
                    ON keeper.RenterId = loser.RenterId AND keeper.Rn = 1
                WHERE loser.Rn > 1;

                -- Re-point every message on a duplicate thread onto its member's keeper thread.
                -- No message is deleted.
                UPDATE cm
                SET cm.ConversationId = map.KeeperId
                FROM ChatMessages cm
                JOIN #ModerationMap map ON cm.ConversationId = map.LoserId;

                -- Re-point participant rows too. ConversationParticipants has its own unique
                -- (ConversationId, UserId) index, so if the keeper already has a row for that
                -- user, drop the duplicate's row instead of moving it (moving would collide).
                DELETE cp
                FROM ConversationParticipants cp
                JOIN #ModerationMap map ON cp.ConversationId = map.LoserId
                WHERE EXISTS (
                    SELECT 1 FROM ConversationParticipants keep
                    WHERE keep.ConversationId = map.KeeperId AND keep.UserId = cp.UserId
                );

                UPDATE cp
                SET cp.ConversationId = map.KeeperId
                FROM ConversationParticipants cp
                JOIN #ModerationMap map ON cp.ConversationId = map.LoserId;

                -- Recompute the keeper's denormalised last-message preview: a duplicate thread's
                -- last message may be more recent than the keeper's own now that messages have
                -- been merged onto it.
                ;WITH LastMessagePerKeeper AS (
                    SELECT
                        cm.ConversationId,
                        cm.Id,
                        cm.Body,
                        cm.CreatedAt,
                        ROW_NUMBER() OVER (PARTITION BY cm.ConversationId ORDER BY cm.CreatedAt DESC, cm.Id DESC) AS Rn
                    FROM ChatMessages cm
                    WHERE cm.ConversationId IN (SELECT DISTINCT KeeperId FROM #ModerationMap)
                )
                UPDATE c
                SET c.LastMessageId = lm.Id,
                    c.LastMessageSnippet = LEFT(lm.Body, 500),
                    c.LastMessageAt = lm.CreatedAt
                FROM Conversations c
                JOIN LastMessagePerKeeper lm ON lm.ConversationId = c.Id AND lm.Rn = 1;

                -- The duplicate conversations now have no messages/participants left; remove
                -- them. This is not "deleting a conversation that contains messages" — every
                -- message was re-pointed off it above.
                DELETE c
                FROM Conversations c
                JOIN #ModerationMap map ON c.Id = map.LoserId;

                DROP TABLE #ModerationDup;
                DROP TABLE #ModerationMap;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Conversations_Kind_RenterId",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_Kind_RenterId",
                table: "Conversations",
                columns: new[] { "Kind", "RenterId" },
                unique: true,
                filter: "[Kind] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverts the index only. The Up-side consolidation of pre-existing duplicate
            // Moderation threads is not reversible (and shouldn't be — those duplicates were the
            // bug) so Down does not attempt to recreate them.
            migrationBuilder.DropIndex(
                name: "IX_Conversations_Kind_RenterId",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_Kind_RenterId",
                table: "Conversations",
                columns: new[] { "Kind", "RenterId" });
        }
    }
}
