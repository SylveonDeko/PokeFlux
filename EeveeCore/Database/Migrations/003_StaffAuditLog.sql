-- Migration: 003_StaffAuditLog.sql

CREATE TABLE public.staff_audit_log (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    staff_id bigint NOT NULL,
    staff_name text,
    action text NOT NULL,
    target_user_id bigint,
    details text,
    reason text NOT NULL,
    created_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL
);

CREATE INDEX staff_audit_log_created_at_idx ON public.staff_audit_log (created_at DESC);
CREATE INDEX staff_audit_log_staff_id_idx ON public.staff_audit_log (staff_id);
CREATE INDEX staff_audit_log_target_user_id_idx ON public.staff_audit_log (target_user_id);

CREATE TRIGGER fill_insert_defaults BEFORE INSERT ON public.staff_audit_log
    FOR EACH ROW EXECUTE FUNCTION public.fill_insert_defaults();
