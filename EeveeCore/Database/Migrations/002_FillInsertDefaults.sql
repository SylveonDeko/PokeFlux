-- Inserts send every mapped column, so NULLs from unset model properties replace the column defaults.
CREATE FUNCTION public.fill_insert_defaults() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    col record;
    filled jsonb := '{}';
    value jsonb;
BEGIN
    FOR col IN
        SELECT a.attname, pg_get_expr(d.adbin, d.adrelid) AS expr
        FROM pg_attribute a
        JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE a.attrelid = TG_RELID AND a.attnotnull AND NOT a.attisdropped
    LOOP
        IF to_jsonb(NEW) -> col.attname = 'null'::jsonb THEN
            EXECUTE 'SELECT to_jsonb(' || col.expr || ')' INTO value;
            filled := filled || jsonb_build_object(col.attname, value);
        END IF;
    END LOOP;

    IF filled <> '{}' THEN
        NEW := jsonb_populate_record(NEW, filled);
    END IF;
    RETURN NEW;
END
$$;

DO $$
DECLARE
    tbl regclass;
BEGIN
    FOR tbl IN
        SELECT DISTINCT a.attrelid::regclass
        FROM pg_attribute a
        JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind = 'r' AND a.attnotnull AND NOT a.attisdropped
    LOOP
        EXECUTE format('CREATE TRIGGER fill_insert_defaults BEFORE INSERT ON %s FOR EACH ROW EXECUTE FUNCTION public.fill_insert_defaults()', tbl);
    END LOOP;
END
$$;
