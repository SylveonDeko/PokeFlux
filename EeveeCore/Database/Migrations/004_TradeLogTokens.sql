-- Migration: 004_TradeLogTokens.sql

ALTER TABLE public.trade_logs ADD COLUMN IF NOT EXISTS sender_tokens jsonb;
ALTER TABLE public.trade_logs ADD COLUMN IF NOT EXISTS receiver_tokens jsonb;
