-- grandfather-unverified-data-owners.sql — ADR-028 section 13 (risk R10)
--
-- WHEN: only after production was ROLLED BACK from the email-verification release
-- (migration 20261009104046_AddEmailVerification) to pre-verification code, and BEFORE
-- rolling FORWARD again. Never needed on a normal deploy: the migration itself already
-- grandfathers every user that exists at the moment it is applied.
--
-- WHY: the schema stays forward during a rollback (EF Down is never run; Down would not
-- un-verify anyone anyway). Pre-verification code creates users with IsEmailConfirmed = 0
-- and lets them log in, so during the rollback window such a user can create listings,
-- bookings, chats, reviews and favourites. After the roll-forward that user would be a
-- "pending registration" (ADR-028 D2): login answers 403, and a re-registration by ANYONE
-- with that email replaces the password and profile and inherits the data. This script
-- marks exactly those users as verified, so they keep their account and their data.
--
-- WHAT: IsEmailConfirmed = 1 for every user with IsEmailConfirmed = 0 who owns or authored
-- at least one row in Listings, Bookings, Favorites, Conversations, ConversationParticipants,
-- ChatMessages, ToyReviews, OwnerReviews, RenterReviews or Reports.
--   * EmailConfirmedAt is deliberately left NULL: NULL together with IsEmailConfirmed = 1
--     is the "grandfathered" marker (ADR-028 section 11, D8).
--   * Users without any such row stay unverified — they are ordinary pending
--     registrations and can verify through the normal link / resend flow.
--   * Blocked users are included: verification does not unblock anyone.
--   * Notifications and moderation-log rows are NOT ownership (they are written about a
--     user by someone else) and do not qualify on their own.
--
-- IDEMPOTENT: the predicate IsEmailConfirmed = 0 makes a second run a no-op (affected 0).
-- Safe to run twice: once right before `up -d` of the verification release, and once right
-- after it, to catch a user who gained data in the minutes between the two (after the
-- roll-forward the gate stops unverified users from gaining new data, so the second run is
-- final).
--
-- HOW (on the server, from /opt/dorent/rental-api; the password travels only through the
-- SQLCMDPASSWORD environment variable — exported, and passed to docker BY NAME ONLY, so the
-- value is in no argv; never -P; -b makes any SQL error fail sqlcmd; -I sets QUOTED_IDENTIFIER):
--
--   export SQLCMDPASSWORD="$(grep -E '^MSSQL_SA_PASSWORD=' .env | tail -n1 | cut -d= -f2-)"
--   docker compose -f docker-compose.production.yml exec -T -e SQLCMDPASSWORD db \
--     /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -I -d RentalPlatformDb \
--     < deploy/grandfather-unverified-data-owners.sql
--   unset SQLCMDPASSWORD
--
-- Expected output: two lines, "grandfather: unverified before=<N>, marked verified=<M>"
-- and "grandfather: still unverified (pending registrations without data)=<N-M>".
-- Prints counts only, never emails or ids. Take a backup first (DEPLOY-PRODUCTION.md).
-- Rehearsed on the restored-backup database during the release (DEPLOY-PRODUCTION.md,
-- "Релиз email-верификации", step 4c) — not on production before it is needed.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @db SYSNAME = DB_NAME();
DECLARE @before INT;
DECLARE @affected INT;

-- The email-verification schema must be present: this script only makes sense on a database
-- the verification migration has already been applied to (it stays applied during a rollback).
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
   OR COL_LENGTH(N'dbo.Users', N'IsEmailConfirmed') IS NULL
   OR COL_LENGTH(N'dbo.Users', N'EmailConfirmedAt') IS NULL
BEGIN
    RAISERROR(N'grandfather: dbo.Users with IsEmailConfirmed/EmailConfirmedAt not found in database [%s]; wrong database (expected -d RentalPlatformDb) or the verification migration was never applied. Nothing changed.', 16, 1, @db);
    RETURN;
END;

BEGIN TRANSACTION;

-- UPDLOCK + HOLDLOCK: no concurrent writer can flip a row between the count and the update.
SELECT @before = COUNT(*)
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
WHERE IsEmailConfirmed = 0;

UPDATE u
SET u.IsEmailConfirmed = 1
FROM dbo.Users AS u
WHERE u.IsEmailConfirmed = 0
  AND (
         EXISTS (SELECT 1 FROM dbo.Listings                 AS x WHERE x.OwnerId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.Bookings                 AS x WHERE x.RenterId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.Favorites                AS x WHERE x.UserId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.Conversations            AS x WHERE x.RenterId = u.Id OR x.OwnerId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.ConversationParticipants AS x WHERE x.UserId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.ChatMessages             AS x WHERE x.SenderId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.ToyReviews               AS x WHERE x.ReviewerId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.OwnerReviews             AS x WHERE x.ReviewerId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.RenterReviews            AS x WHERE x.ReviewerId = u.Id)
      OR EXISTS (SELECT 1 FROM dbo.Reports                  AS x WHERE x.ReporterUserId = u.Id)
  );

SET @affected = @@ROWCOUNT;

COMMIT TRANSACTION;

PRINT CONCAT(N'grandfather: unverified before=', @before, N', marked verified=', @affected);
PRINT CONCAT(N'grandfather: still unverified (pending registrations without data)=', @before - @affected);
