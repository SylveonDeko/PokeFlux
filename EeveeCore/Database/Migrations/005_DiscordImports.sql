-- Migration: 005_DiscordImports.sql

CREATE TABLE IF NOT EXISTS public.discord_imports
(
    discord_id  bigint PRIMARY KEY,
    fluxer_id   bigint      NOT NULL UNIQUE,
    imported_at timestamptz NOT NULL DEFAULT now(),
    pokemon     integer     NOT NULL DEFAULT 0,
    parties     integer     NOT NULL DEFAULT 0,
    milestones  integer     NOT NULL DEFAULT 0,
    details     jsonb       NOT NULL DEFAULT '{}'::jsonb
);
