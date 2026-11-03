-- Baseline schema for a new EeveeFluxer database, taken from EeveeCore's schema after its migrations up to
-- 018_AddBreedingFemaleToOwnership. Later changes go in new numbered scripts.
-- Migration: 001_Baseline.sql

SET check_function_bodies = false;

--
-- Name: calculate_trading_velocity(bigint, interval); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.calculate_trading_velocity(p_user_id bigint, p_time_window interval) RETURNS TABLE(trade_count integer, unique_partners integer, avg_interval double precision)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    WITH user_trades AS (
        SELECT 
            time,
            CASE 
                WHEN sender = p_user_id THEN receiver 
                ELSE sender 
            END AS partner_id
        FROM trade_logs
        WHERE (sender = p_user_id OR receiver = p_user_id)
            AND time >= CURRENT_TIMESTAMP - p_time_window
        ORDER BY time
    ),
    trade_intervals AS (
        SELECT 
            time - LAG(time) OVER (ORDER BY time) AS interval
        FROM user_trades
    )
    SELECT 
        COUNT(*)::INTEGER AS trade_count,
        COUNT(DISTINCT partner_id)::INTEGER AS unique_partners,
        COALESCE(AVG(EXTRACT(EPOCH FROM interval)), 0)::DOUBLE PRECISION AS avg_interval
    FROM user_trades
    LEFT JOIN trade_intervals ON TRUE;
END;
$$;


--
-- Name: detect_circular_trading(bigint, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.detect_circular_trading(p_user_id bigint, p_depth integer DEFAULT 5) RETURNS TABLE(path_users bigint[], path_length integer, is_circular boolean)
    LANGUAGE sql
    AS $$
WITH RECURSIVE trade_paths AS (
    -- Base case: start from the given user
    SELECT 
        ARRAY[p_user_id] AS path,
        p_user_id AS current_user,
        1 AS depth
    
    UNION ALL
    
    -- Recursive case: follow trade connections
    SELECT 
        tp.path || t.other_user,
        t.other_user,
        tp.depth + 1
    FROM trade_paths tp
    CROSS JOIN LATERAL (
        SELECT DISTINCT
            CASE 
                WHEN sender = tp.current_user THEN receiver 
                ELSE sender 
            END AS other_user
        FROM trade_logs
        WHERE (sender = tp.current_user OR receiver = tp.current_user)
            AND time >= CURRENT_TIMESTAMP - INTERVAL '7 days'
    ) t
    WHERE tp.depth < p_depth
        AND NOT t.other_user = ANY(tp.path[2:]) -- Avoid revisiting nodes (except first)
)
SELECT 
    path AS path_users,
    array_length(path, 1) AS path_length,
    path[1] = path[array_length(path, 1)] AS is_circular
FROM trade_paths
WHERE array_length(path, 1) >= 3
    AND (path[1] = path[array_length(path, 1)] OR array_length(path, 1) = p_depth);
$$;


--
-- Name: dropnull(character varying); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.dropnull(character varying) RETURNS integer
    LANGUAGE plpgsql
    AS $_$
DECLARE
columnName varchar(50);
BEGIN

FOR columnName IN 

select a.attname
from pg_catalog.pg_attribute a
where attrelid = $1::regclass
and a.attnum > 0
and not a.attisdropped
and a.attnotnull and a.attname not in(

SELECT 
pg_attribute.attname
FROM pg_index, pg_class, pg_attribute 
WHERE 
pg_class.oid = $1::regclass AND
indrelid = pg_class.oid AND
pg_attribute.attrelid = pg_class.oid AND 
pg_attribute.attnum = any(pg_index.indkey)
AND indisprimary)

LOOP
EXECUTE 'ALTER TABLE ' || $1 ||' ALTER COLUMN '||columnName||' DROP NOT NULL'; 
END LOOP;
RAISE NOTICE 'Done removing the NOT NULL Constraints for TABLE: %', $1;
RETURN 1;
END;
$_$;


--
-- Name: equal_bool_int(boolean, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.equal_bool_int(x boolean, y integer) RETURNS boolean
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $$ 
begin
    return x::int = y;
end;
$$;


--
-- Name: equal_int_bool(integer, boolean); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.equal_int_bool(x integer, y boolean) RETURNS boolean
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $$ 
begin
    return x = y::int;
end;
$$;


--
-- Name: expire_cooldown(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.expire_cooldown() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  DELETE FROM mothers WHERE entry_time < NOW() - INTERVAL '6 hours';
  RETURN NEW;
END;
$$;


--
-- Name: get_migration_stats(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.get_migration_stats() RETURNS TABLE(total_users bigint, total_pokemon_references bigint, valid_pokemon_references bigint, invalid_pokemon_references bigint, users_with_invalid_refs bigint, duplicate_pokemon_count bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    SELECT
        (SELECT COUNT(*) FROM users) AS total_users,
        (SELECT COUNT(*) FROM (
            SELECT unnest(pokes) FROM users WHERE pokes IS NOT NULL AND array_length(pokes, 1) > 0
        ) AS x WHERE unnest != 0) AS total_pokemon_references,
        (SELECT COUNT(*) FROM user_pokemon_ownership) AS valid_pokemon_references,
        (SELECT COUNT(*) FROM invalid_pokemon_references) AS invalid_pokemon_references,
        (SELECT COUNT(DISTINCT user_id) FROM invalid_pokemon_references) AS users_with_invalid_refs,
        (SELECT COUNT(*) FROM (
            SELECT pokemon_id FROM user_pokemon_ownership GROUP BY pokemon_id HAVING COUNT(*) > 1
        ) AS dups) AS duplicate_pokemon_count;
END;
$$;


--
-- Name: get_user_pokemon(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.get_user_pokemon(user_id_param bigint) RETURNS TABLE(pokemon_id bigint, "position" integer, pokemon_name text, is_canonical_owner boolean)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    SELECT 
        upo.pokemon_id,
        upo."position",
        p.pokname,
        CASE WHEN p.owner = upo.user_id THEN TRUE ELSE FALSE END
    FROM user_pokemon_ownership upo
    JOIN pokes p ON upo.pokemon_id = p.id
    WHERE upo.user_id = user_id_param
    ORDER BY upo."position";
END;
$$;


--
-- Name: hatchery_counter(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.hatchery_counter(user_id bigint) RETURNS text[]
    LANGUAGE plpgsql
    AS $_$
DECLARE
    bike BOOLEAN;
    hatchery_pokemon INTEGER[];
    hatch_counter INTEGER;
    pokename TEXT;
    step_count INTEGER;
    mother_name TEXT;
    hatched_names text[];
    _id INTEGER;
    group_number INTEGER;
    column_index INTEGER;

BEGIN
    SELECT users.bike into bike from users WHERE users.u_id = user_id;
    SELECT egg_hatchery."group" INTO group_number FROM egg_hatchery WHERE egg_hatchery.u_id = user_id;
    SELECT ARRAY[egg_hatchery."1", egg_hatchery."2", egg_hatchery."3", egg_hatchery."4", egg_hatchery."5",
                 egg_hatchery."6", egg_hatchery."7", egg_hatchery."8", egg_hatchery."9", egg_hatchery."10"]
    INTO hatchery_pokemon
    FROM egg_hatchery WHERE egg_hatchery.u_id = user_id AND egg_hatchery."group" = 1;

    FOREACH _id IN ARRAY hatchery_pokemon
    LOOP
        SELECT pokes.counter INTO hatch_counter FROM pokes WHERE pokes.id = _id;
        SELECT pokes.pokname INTO pokename FROM pokes WHERE pokes.id = _id;
        SELECT pokes.name INTO mother_name FROM pokes WHERE pokes.id = _id;

        IF (hatch_counter > 1) THEN
            IF (bike = true) THEN
                step_count := 2;
            ELSE
                step_count := 1;
            END IF;



            UPDATE pokes SET counter = counter - (1 * step_count) WHERE pokes.id = _id;
        ELSIF (hatch_counter <= 1 AND pokename = 'Egg'::text) THEN
            UPDATE pokes SET pokname = mother_name WHERE id = _id;
            UPDATE users SET daycare = daycare - 1 WHERE users.u_id = user_id;
            FOR column_index IN 1..10 LOOP
                EXECUTE format('UPDATE egg_hatchery SET "%s" = NULL WHERE u_id = $1 AND "%s" = $2', column_index, column_index)
                USING user_id, _id;
            END LOOP;
            hatched_names = array_append(hatched_names, mother_name);
        END IF;
    END LOOP;
    RETURN hatched_names;
END;
$_$;


--
-- Name: hatchery_counter2(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.hatchery_counter2(user_id bigint) RETURNS text[]
    LANGUAGE plpgsql
    AS $_$
DECLARE
    bike BOOLEAN;
    hatchery_pokemon INTEGER[];
    hatch_counter INTEGER;
    pokename TEXT;
    step_count INTEGER;
    mother_name TEXT;
    hatched_names text[];
    _id INTEGER;
    group_number INTEGER;
    column_index INTEGER;

BEGIN
    SELECT users.bike into bike from users WHERE users.u_id = user_id;
    SELECT egg_hatchery."group" INTO group_number FROM egg_hatchery WHERE egg_hatchery.u_id = user_id;
    SELECT ARRAY[egg_hatchery."1", egg_hatchery."2", egg_hatchery."3", egg_hatchery."4", egg_hatchery."5",
                 egg_hatchery."6", egg_hatchery."7", egg_hatchery."8", egg_hatchery."9", egg_hatchery."10"]
    INTO hatchery_pokemon
    FROM egg_hatchery WHERE egg_hatchery.u_id = user_id AND egg_hatchery."group" = 2;

    FOREACH _id IN ARRAY hatchery_pokemon
    LOOP
        SELECT pokes.counter INTO hatch_counter FROM pokes WHERE pokes.id = _id;
        SELECT pokes.pokname INTO pokename FROM pokes WHERE pokes.id = _id;
        SELECT pokes.name INTO mother_name FROM pokes WHERE pokes.id = _id;

        IF (hatch_counter > 1) THEN
            step_count := 0;
            IF (random() < 1.0 / 3.0) THEN
                step_count := 1;  
            END IF;



            UPDATE pokes SET counter = counter - (1 * step_count) WHERE pokes.id = _id;
        ELSIF (hatch_counter <= 1 AND pokename = 'Egg'::text) THEN
            UPDATE pokes SET pokname = mother_name WHERE id = _id;
            UPDATE users SET daycare = daycare - 1 WHERE users.u_id = user_id;
            FOR column_index IN 1..10 LOOP
                EXECUTE format('UPDATE egg_hatchery SET "%s" = NULL WHERE u_id = $1 AND "%s" = $2', column_index, column_index)
                USING user_id, _id;
            END LOOP;
            hatched_names = array_append(hatched_names, mother_name);
        END IF;
    END LOOP;
    RETURN hatched_names;
END;
$_$;


--
-- Name: hatchery_counter3(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.hatchery_counter3(user_id bigint) RETURNS text[]
    LANGUAGE plpgsql
    AS $_$
DECLARE
    bike BOOLEAN;
    hatchery_pokemon INTEGER[];
    hatch_counter INTEGER;
    pokename TEXT;
    step_count INTEGER;
    mother_name TEXT;
    hatched_names text[];
    _id INTEGER;
    group_number INTEGER;
    column_index INTEGER;

BEGIN
    SELECT users.bike into bike from users WHERE users.u_id = user_id;
    SELECT egg_hatchery."group" INTO group_number FROM egg_hatchery WHERE egg_hatchery.u_id = user_id;
    SELECT ARRAY[egg_hatchery."1", egg_hatchery."2", egg_hatchery."3", egg_hatchery."4", egg_hatchery."5",
                 egg_hatchery."6", egg_hatchery."7", egg_hatchery."8", egg_hatchery."9", egg_hatchery."10"]
    INTO hatchery_pokemon
    FROM egg_hatchery WHERE egg_hatchery.u_id = user_id AND egg_hatchery."group" = 3;

    FOREACH _id IN ARRAY hatchery_pokemon
    LOOP
        SELECT pokes.counter INTO hatch_counter FROM pokes WHERE pokes.id = _id;
        SELECT pokes.pokname INTO pokename FROM pokes WHERE pokes.id = _id;
        SELECT pokes.name INTO mother_name FROM pokes WHERE pokes.id = _id;

        IF (hatch_counter > 1) THEN
            step_count := 0;
            IF (random() < 1.0 / 6.0) THEN
                step_count := 1;  
            END IF;



            UPDATE pokes SET counter = counter - (1 * step_count) WHERE pokes.id = _id;
        ELSIF (hatch_counter <= 1 AND pokename = 'Egg'::text) THEN
            UPDATE pokes SET pokname = mother_name WHERE id = _id;
            UPDATE users SET daycare = daycare - 1 WHERE users.u_id = user_id;
            FOR column_index IN 1..10 LOOP
                EXECUTE format('UPDATE egg_hatchery SET "%s" = NULL WHERE u_id = $1 AND "%s" = $2', column_index, column_index)
                USING user_id, _id;
            END LOOP;
            hatched_names = array_append(hatched_names, mother_name);
        END IF;
    END LOOP;
    RETURN hatched_names;
END;
$_$;


--
-- Name: insert_egg_hatchery(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.insert_egg_hatchery() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  INSERT INTO egg_hatchery (u_id, "group") VALUES (NEW.u_id, 1), (NEW.u_id, 2), (NEW.u_id, 3);
  RETURN NEW;
END;
$$;


--
-- Name: level_pokemon(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.level_pokemon(user_id bigint) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE
    _id BIGINT;
    pokename TEXT;
    nick TEXT;
    held_item TEXT;
    happiness INTEGER;
    friendship_chance INTEGER;
    --
    expcap INTEGER;
    experience INTEGER;
    affection INTEGER;
    pokemon_level INTEGER;
    added_exp INTEGER;
    exp_boost INTEGER DEFAULT 1;

begin

  SELECT users.selected INTO _id FROM users WHERE users.u_id = user_id;

  SELECT pokes.pokname,  pokes.hitem, pokes.happiness, pokes.expcap, pokes.exp, pokes.pokelevel INTO pokename, held_item, happiness, expcap, experience, pokemon_level FROM pokes WHERE pokes.id = _id;
    IF pokename = 'Egg' THEN
        RETURN nick;
    END IF;
  SELECT floor(random() * 10 + 1)::int INTO friendship_chance;

    IF (friendship_chance < 4) THEN
        IF (held_item = 'soothe-bell') THEN
            UPDATE pokes SET happiness = pokes.happiness + floor(friendship_chance * 1.2)::int WHERE pokes.id = _id;
        ELSE
            UPDATE pokes SET happiness = pokes.happiness + friendship_chance WHERE pokes.id = _id;
        END IF;
    END IF;

    IF (held_item = 'xp-block') THEN
        RETURN nick;
    END IF;

    -- calculate exp to add
    IF (held_item = 'lucky-egg') THEN
        exp_boost = exp_boost * 1.5;
    END IF;

    IF (affection >= 2) THEN
        exp_boost = exp_boost * 1.2;
    END IF;

    added_exp := (SELECT floor(
        (
            (
                (1 * 100 * exp_boost * 1 * 1 * pokemon_level) 
            ) * 50
        ) / 3000
    )::int);

    RAISE NOTICE '%', added_exp;
    RAISE NOTICE '%', exp_boost;
    RAISE NOTICE '%', pokemon_level;

    IF (added_exp + experience > expcap AND pokemon_level < 100) THEN
        UPDATE pokes SET pokelevel = pokelevel + 1, expcap = pokelevel ^ 2, exp = 0 WHERE pokes.id = _id;

        SELECT CASE WHEN pokes.poknick = 'None' THEN pokes.pokname ELSE pokes.poknick END INTO nick FROM pokes WHERE pokes.id = _id;
    ELSIF (pokemon_level < 100) THEN
        UPDATE pokes SET exp = exp + added_exp WHERE pokes.id = _id;
    END IF;
    RETURN nick;
end;
$$;


--
-- Name: mergearrays(anyarray, anyarray); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mergearrays(a1 anyarray, a2 anyarray) RETURNS anyarray
    LANGUAGE sql STRICT
    AS $_$

    SELECT ARRAY_AGG(x ORDER BY x)
    FROM (
        SELECT DISTINCT UNNEST($1 || $2) AS x
    ) s;

$_$;


--
-- Name: newstats(integer, integer, integer, integer, integer, integer, integer); Type: PROCEDURE; Schema: public; Owner: -
--

CREATE PROCEDURE public.newstats(IN newpokemon integer, IN newhpiv integer, IN newatkiv integer, IN newdefiv integer, IN newspatkiv integer, IN newspdefiv integer, IN newspeediv integer)
    LANGUAGE plpgsql
    AS $$
begin
    -- set new stats
    update pokes 
    set hpiv = newhpiv, atkiv = newatkiv, defiv = newdefiv, spatkiv = newspatkiv, spdefiv = newspdefiv, speediv = newspeediv 
    where id = newpokemon;

    commit;
end;$$;


--
-- Name: owned_vs_unowned(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.owned_vs_unowned() RETURNS text
    LANGUAGE plpgsql
    AS $$DECLARE
    all_pokes BIGINT[];
    unowned_count INT;

BEGIN
    SELECT unnest(users.pokes) FROM   users INTO all_pokes;
    SELECT count(*) FROM pokes WHERE id != ANY (all_pokes) INTO unowned_count;
    RAISE NOTICE 'Pokes: %', all_pokes;
    RAISE NOTICE 'Without owner count: %', unowned_count;
    RAISE NOTICE 'Pokes: %', pg_typeof(all_pokes);
    RETURN 'ok';
END;
$$;


--
-- Name: party_counter(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.party_counter(user_id bigint) RETURNS text[]
    LANGUAGE plpgsql
    AS $$
DECLARE
    bike BOOLEAN;
    party_pokemon BIGINT[6];
    hatch_counter INTEGER;
    pokename TEXT;
    step_count INTEGER;
    mother_name TEXT;
    hatched_names text[];
    _id INTEGER;

BEGIN
    -- 455277032625012737
    SELECT users.bike into bike from users WHERE users.u_id = user_id;
    SELECT users.party into party_pokemon FROM users WHERE users.u_id = user_id;
    FOREACH _id IN ARRAY party_pokemon
    LOOP
        SELECT pokes.counter INTO hatch_counter FROM pokes WHERE pokes.id = _id;
        SELECT pokes.pokname INTO pokename FROM pokes WHERE pokes.id = _id;
        select pokes.name INTO mother_name FROM pokes WHERE pokes.id = _id;

        IF (hatch_counter > 1) THEN
            IF (bike = true) THEN
                step_count := 2;
            ELSE
                step_count := 1;
            END IF;
            UPDATE pokes SET counter = counter - (1 * step_count) WHERE pokes.id = _id;
        ELSIF (hatch_counter <= 1 AND pokename = 'Egg'::text) THEN
            UPDATE pokes SET pokname = mother_name WHERE id = _id;
            UPDATE users SET daycare = daycare - 1 WHERE users.u_id = user_id;
            hatched_names = array_append(hatched_names, mother_name);
        END IF;
    END LOOP;
    RETURN hatched_names;
END;
$$;


--
-- Name: refresh_trading_network_graph(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.refresh_trading_network_graph() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- Refresh asynchronously to avoid blocking
    PERFORM pg_notify('refresh_mv_trading_network', '');
    RETURN NEW;
END;
$$;


--
-- Name: search_whole_db(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.search_whole_db(_like_pattern text) RETURNS TABLE(_tbl regclass, _ctid tid)
    LANGUAGE plpgsql
    AS $_$
BEGIN
   FOR _tbl IN
      SELECT c.oid::regclass
      FROM   pg_class c
      JOIN   pg_namespace n ON n.oid = relnamespace
      WHERE  c.relkind = 'r'                           -- only tables
      AND    n.nspname !~ '^(pg_|information_schema)'  -- exclude system schemas
      ORDER BY n.nspname, c.relname
   LOOP
      RETURN QUERY EXECUTE format(
         'SELECT $1, ctid FROM %s t WHERE t::text ~~ %L'
       , _tbl, '%' || _like_pattern || '%')
      USING _tbl;
   END LOOP;
END
$_$;


--
-- Name: selected_counter(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.selected_counter(user_id bigint) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE
    bike BOOLEAN;
    hatch_counter INTEGER;
    pokename TEXT;
    step_count INTEGER;
    mother_name TEXT;
    hatched_name text;
    _id INTEGER;

BEGIN
    -- 455277032625012737
    SELECT users.bike into bike from users WHERE users.u_id = user_id;
    SELECT users.selected into _id FROM users WHERE users.u_id = user_id;
    
    SELECT pokes.counter INTO hatch_counter FROM pokes WHERE pokes.id = _id;
    SELECT pokes.pokname INTO pokename FROM pokes WHERE pokes.id = _id;
    select pokes.name INTO mother_name FROM pokes WHERE pokes.id = _id;

    IF (hatch_counter > 1) THEN
        IF (bike = true) THEN
            step_count := 2;
        ELSE
            step_count := 1;
        END IF;
        UPDATE pokes SET counter = counter - (1 * step_count) WHERE pokes.id = _id;
    ELSIF (hatch_counter <= 1 AND pokename = 'Egg'::text) THEN
        UPDATE pokes SET pokname = mother_name WHERE id = _id;
        UPDATE users SET daycare = daycare - 1 WHERE users.u_id = user_id;
        hatched_name = mother_name;
    END IF;
    RETURN hatched_name;
END;
$$;


--
-- Name: transfer_pokemon(bigint, bigint, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.transfer_pokemon(from_user_id bigint, to_user_id bigint, pokemon_id_param bigint) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
DECLARE
    ownership_exists BOOLEAN;
    current_position INTEGER;
    max_position INTEGER;
    is_owner BOOLEAN;
BEGIN
    -- Check if the source user owns this Pokemon
    SELECT EXISTS(
        SELECT 1 FROM user_pokemon_ownership 
        WHERE user_id = from_user_id AND pokemon_id = pokemon_id_param
    ) INTO ownership_exists;
    
    IF NOT ownership_exists THEN
        RETURN FALSE;
    END IF;
    
    -- Check if this user is the canonical owner
    SELECT (owner = from_user_id) INTO is_owner
    FROM pokes
    WHERE id = pokemon_id_param;
    
    -- Get the position for removal
    SELECT "position" INTO current_position
    FROM user_pokemon_ownership
    WHERE user_id = from_user_id AND pokemon_id = pokemon_id_param;
    
    -- Get max position for the target user
    SELECT COALESCE(MAX("position"), -1) INTO max_position
    FROM user_pokemon_ownership
    WHERE user_id = to_user_id;
    
    -- Begin transaction for the transfer
    BEGIN
        -- Remove from original user
        DELETE FROM user_pokemon_ownership
        WHERE user_id = from_user_id AND pokemon_id = pokemon_id_param;
        
        -- Shift positions for the source user to close the gap
        UPDATE user_pokemon_ownership
        SET "position" = "position" - 1
        WHERE user_id = from_user_id AND "position" > current_position;
        
        -- Add to target user
        INSERT INTO user_pokemon_ownership (user_id, pokemon_id, "position")
        VALUES (to_user_id, pokemon_id_param, max_position + 1);
        
        -- If this was the canonical owner, update the Pokemon record
        IF is_owner THEN
            UPDATE pokes
            SET owner = to_user_id
            WHERE id = pokemon_id_param;
        END IF;
        
        RETURN TRUE;
    EXCEPTION
        WHEN OTHERS THEN
            RAISE NOTICE 'Error transferring Pokemon: %', SQLERRM;
            RETURN FALSE;
    END;
END;
$$;


--
-- Name: update_eggs_array(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.update_eggs_array() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.eggs := ARRAY[
        NEW."1", NEW."2", NEW."3", NEW."4", NEW."5",
        NEW."6", NEW."7", NEW."8", NEW."9", NEW."10"
    ];
    RETURN NEW;
END;
$$;


--
-- Name: update_market_updated_at(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.update_market_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: users; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.users (
    u_id bigint,
    redeems integer,
    evpoints integer,
    tnick text,
    upvotepoints integer,
    mewcoins bigint,
    user_order text DEFAULT 'kek'::character varying,
    pokes integer[],
    inventory jsonb DEFAULT '{}'::jsonb,
    items jsonb DEFAULT '{}'::jsonb,
    daycare integer,
    daycarelimit integer DEFAULT 1,
    energy integer DEFAULT 10,
    held_item text DEFAULT 'None'::text,
    fishing_exp bigint DEFAULT 0,
    fishing_level integer DEFAULT 1,
    energy_hour integer,
    fishing_level_cap bigint DEFAULT 50,
    luck integer DEFAULT 1,
    npc_relationships jsonb DEFAULT '{}'::jsonb,
    selected integer,
    visible boolean DEFAULT true,
    silenced boolean DEFAULT false NOT NULL,
    tradelock boolean DEFAULT false,
    voted boolean DEFAULT false,
    id integer NOT NULL,
    restored boolean DEFAULT false,
    bike boolean DEFAULT false,
    botbanned boolean DEFAULT false,
    marketlimit integer DEFAULT 3 NOT NULL,
    staff text DEFAULT 'User'::text NOT NULL,
    gym_leader boolean DEFAULT false,
    oxiconverted boolean DEFAULT false NOT NULL,
    event integer DEFAULT 0,
    last_vote integer DEFAULT 0 NOT NULL,
    vote_streak integer DEFAULT 0 NOT NULL,
    skins jsonb DEFAULT '{}'::jsonb NOT NULL,
    holidayinv jsonb DEFAULT '{}'::jsonb,
    raffle integer DEFAULT 0,
    region text DEFAULT 'original'::text NOT NULL,
    hunt text DEFAULT ''::text,
    chain integer DEFAULT 0 NOT NULL,
    patreon_override character varying,
    note character varying,
    skin_tokens integer DEFAULT 0 NOT NULL,
    comp boolean DEFAULT false,
    show_donations boolean DEFAULT false,
    mystery_token integer DEFAULT 0,
    event_skin_tokens integer DEFAULT 0,
    os_rep integer DEFAULT 0 NOT NULL,
    vip_tokens integer DEFAULT 0 NOT NULL,
    active boolean DEFAULT false NOT NULL,
    slime integer DEFAULT 0,
    level integer DEFAULT 1,
    current_xp integer DEFAULT 0,
    crystal_slime integer DEFAULT 0,
    ancient_unlocked boolean DEFAULT false,
    selected_title text DEFAULT ''::text,
    start_progress jsonb DEFAULT '{"Tutorial Mission": {"Stage1": false, "Stage2": false, "Stage3": false, "Stage4": false}}'::jsonb NOT NULL,
    voucher integer DEFAULT 0,
    tokens jsonb DEFAULT '{"Bug": 0, "Ice": 0, "Dark": 0, "Fire": 0, "Rock": 0, "Fairy": 0, "Ghost": 0, "Grass": 0, "Steel": 0, "Water": 0, "Dragon": 0, "Flying": 0, "Ground": 0, "Normal": 0, "Poison": 0, "Psychic": 0, "Electric": 0, "Fighting": 0}'::jsonb,
    patreon text DEFAULT 'None'::text,
    type_tokens numeric(20,0)[],
    titles text[],
    alt numeric(20,0)[],
    trade_banned boolean DEFAULT false NOT NULL,
    trade_ban_reason text,
    trade_ban_date timestamp without time zone,
    trade_ban_fraud_id integer,
    market_banned boolean DEFAULT false NOT NULL,
    market_ban_reason text,
    market_ban_date timestamp without time zone,
    CONSTRAINT type_tokens_length_check CHECK ((array_length(type_tokens, 1) = 18)),
    CONSTRAINT users_check CHECK (((redeems >= 0) AND (mewcoins >= 0) AND (evpoints >= 0) AND (energy >= 0)))
);


--
-- Name: users_check(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.users_check(u_id_param bigint) RETURNS SETOF public.users
    LANGUAGE plpgsql STABLE
    AS $$
BEGIN
    RETURN QUERY SELECT u.*
    FROM users u
    WHERE u.alt = u_id_param;

    IF NOT FOUND THEN
        RETURN QUERY SELECT u.*
        FROM users u
        WHERE u.u_id = u_id_param;
    END IF;
END;
$$;


--
-- Name: =; Type: OPERATOR; Schema: public; Owner: -
--

CREATE OPERATOR public.= (
    FUNCTION = public.equal_bool_int,
    LEFTARG = boolean,
    RIGHTARG = integer
);


--
-- Name: =; Type: OPERATOR; Schema: public; Owner: -
--

CREATE OPERATOR public.= (
    FUNCTION = public.equal_int_bool,
    LEFTARG = integer,
    RIGHTARG = boolean
);


--
-- Name: ActiveSpawns; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ActiveSpawns" (
    "MessageId" bigint NOT NULL,
    "ChannelId" bigint NOT NULL,
    "GuildId" bigint NOT NULL,
    "PokemonName" character varying(255) NOT NULL,
    "IsShiny" boolean DEFAULT false NOT NULL,
    "LegendaryChance" integer DEFAULT 0 NOT NULL,
    "UltraBeastChance" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
    "IsCaught" boolean DEFAULT false NOT NULL,
    "CaughtByUserId" bigint,
    "CaughtAt" timestamp with time zone
);


--
-- Name: UserFilterCriteria; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."UserFilterCriteria" (
    "Id" integer NOT NULL,
    "FilterGroupId" integer NOT NULL,
    "FieldName" character varying(50) NOT NULL,
    "Operator" character varying(20) NOT NULL,
    "ValueText" text,
    "ValueNumeric" integer,
    "ValueNumericMax" integer,
    "ValueBoolean" boolean,
    "LogicalConnector" character varying(3),
    "CriterionOrder" integer DEFAULT 0 NOT NULL
);


--
-- Name: UserFilterCriteria_Id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public."UserFilterCriteria_Id_seq"
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: UserFilterCriteria_Id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public."UserFilterCriteria_Id_seq" OWNED BY public."UserFilterCriteria"."Id";


--
-- Name: UserFilterGroups; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."UserFilterGroups" (
    "Id" integer NOT NULL,
    "UserId" bigint NOT NULL,
    "Name" character varying(100) NOT NULL,
    "Description" character varying(500),
    "Color" character varying(7) DEFAULT '#3B82F6'::character varying NOT NULL,
    "Icon" character varying(10) DEFAULT '📁'::character varying NOT NULL,
    "IsFavorite" boolean DEFAULT false NOT NULL,
    "SortOrder" integer DEFAULT 0 NOT NULL,
    "IsActive" boolean DEFAULT true NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
    "UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: UserFilterGroups_Id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public."UserFilterGroups_Id_seq"
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: UserFilterGroups_Id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public."UserFilterGroups_Id_seq" OWNED BY public."UserFilterGroups"."Id";


--
-- Name: achievements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.achievements (
    duel_party_wins integer DEFAULT 0,
    duel_single_wins integer DEFAULT 0,
    npc_wins integer DEFAULT 0,
    first_party_create boolean DEFAULT false NOT NULL,
    fishing_success integer DEFAULT 0,
    breed_hexa integer DEFAULT 0,
    breed_penta integer DEFAULT 0,
    breed_success integer DEFAULT 0,
    market_sold integer DEFAULT 0,
    pokemon_caught integer DEFAULT 0,
    shiny_caught integer DEFAULT 0,
    shadow_caught integer DEFAULT 0,
    chests_legend integer DEFAULT 0,
    chests_mythic integer DEFAULT 0,
    chests_rare integer DEFAULT 0,
    chests_common integer DEFAULT 0,
    redeems_used integer DEFAULT 0,
    u_id bigint NOT NULL,
    missions integer DEFAULT 0,
    votes integer DEFAULT 0,
    donation_amount integer DEFAULT 0,
    pokemon_normal integer DEFAULT 0,
    pokemon_fire integer DEFAULT 0,
    pokemon_water integer DEFAULT 0,
    pokemon_grass integer DEFAULT 0,
    pokemon_electric integer DEFAULT 0,
    pokemon_ice integer DEFAULT 0,
    pokemon_fighting integer DEFAULT 0,
    pokemon_poison integer DEFAULT 0,
    pokemon_ground integer DEFAULT 0,
    pokemon_flying integer DEFAULT 0,
    pokemon_psychic integer DEFAULT 0,
    pokemon_bug integer DEFAULT 0,
    pokemon_rock integer DEFAULT 0,
    pokemon_ghost integer DEFAULT 0,
    pokemon_dark integer DEFAULT 0,
    pokemon_dragon integer DEFAULT 0,
    pokemon_steel integer DEFAULT 0,
    pokemon_fairy integer DEFAULT 0,
    unown_event integer DEFAULT 0,
    npc_duels integer DEFAULT 0,
    duels_total integer DEFAULT 0,
    pokemon_released integer DEFAULT 0,
    dex_complete boolean DEFAULT false,
    duel_inverse_wins integer DEFAULT 0,
    shiny_bred integer DEFAULT 0,
    shadow_bred integer DEFAULT 0,
    chests_voucher integer DEFAULT 0,
    market_purchased integer DEFAULT 0,
    gym_wins integer DEFAULT 0,
    easter_completed smallint DEFAULT 0 NOT NULL,
    wombo_used integer DEFAULT 0 NOT NULL,
    breed_titan integer DEFAULT 0,
    breed_quad integer DEFAULT 0,
    duel_party_loses integer DEFAULT 0,
    pokemon_released_ivtotal integer DEFAULT 0 NOT NULL,
    duel_total_xp real DEFAULT 0 NOT NULL,
    game_wordsearch integer DEFAULT 0,
    game_slots integer DEFAULT 0,
    game_slots_win integer DEFAULT 0
);


--
-- Name: ai_generations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ai_generations (
    id integer NOT NULL,
    theme text NOT NULL,
    generated_by bigint,
    "time" timestamp without time zone,
    image_url text,
    pokemon character varying,
    ranked smallint DEFAULT 0,
    featured boolean DEFAULT false
);


--
-- Name: ai_generations_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.ai_generations_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: ai_generations_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.ai_generations_id_seq OWNED BY public.ai_generations.id;


--
-- Name: ai_themes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ai_themes (
    id integer NOT NULL,
    creator bigint,
    featured boolean DEFAULT false,
    name character varying NOT NULL,
    used bigint DEFAULT 0,
    prompt text,
    negative_prompt text DEFAULT ''::text,
    text_cfg smallint DEFAULT 18,
    weight real DEFAULT 0.55,
    steps integer DEFAULT 30,
    style integer DEFAULT 1,
    display_img text
);


--
-- Name: ai_themes_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.ai_themes_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: ai_themes_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.ai_themes_id_seq OWNED BY public.ai_themes.id;


--
-- Name: announce; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.announce (
    id integer NOT NULL,
    announce text,
    staff text,
    "timestamp" integer,
    imp_level integer DEFAULT 1 NOT NULL,
    title character varying DEFAULT 'Bot Announcement'::character varying NOT NULL
);


--
-- Name: announce_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.announce_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: announce_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.announce_id_seq OWNED BY public.announce.id;


--
-- Name: artists; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.artists (
    artist character varying,
    pokemon character varying,
    link character varying,
    id integer NOT NULL,
    in_use boolean DEFAULT false NOT NULL
);


--
-- Name: artists_consent; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.artists_consent (
    artist character varying NOT NULL,
    deviantart character varying,
    instagram character varying,
    twitter character varying,
    discord character varying,
    other_links character varying[],
    date timestamp with time zone,
    comment character varying,
    info character varying,
    id integer NOT NULL,
    contact character varying,
    extra character varying,
    in_use boolean DEFAULT false
);


--
-- Name: artists_consent_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.artists_consent_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: artists_consent_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.artists_consent_id_seq OWNED BY public.artists_consent.id;


--
-- Name: artists_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.artists_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: artists_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.artists_id_seq OWNED BY public.artists.id;


--
-- Name: botbans; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.botbans (
    users bigint[] NOT NULL,
    duelbans bigint[] NOT NULL
);


--
-- Name: burst_trading_patterns; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.burst_trading_patterns (
    id integer NOT NULL,
    user_id bigint NOT NULL,
    detected_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    burst_start timestamp without time zone NOT NULL,
    burst_end timestamp without time zone NOT NULL,
    trade_count integer NOT NULL,
    unique_partners integer NOT NULL,
    average_interval_seconds double precision NOT NULL,
    risk_score double precision NOT NULL,
    pattern_data jsonb
);


--
-- Name: burst_trading_patterns_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.burst_trading_patterns_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: burst_trading_patterns_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.burst_trading_patterns_id_seq OWNED BY public.burst_trading_patterns.id;


--
-- Name: cal; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.cal (
    id integer NOT NULL,
    week integer,
    monday integer,
    tuesday integer,
    wednesday integer,
    thursday integer,
    friday integer,
    saturday integer,
    sunday integer
);


--
-- Name: cal_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.cal_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: cal_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.cal_id_seq OWNED BY public.cal.id;


--
-- Name: cheststore; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.cheststore (
    u_id bigint NOT NULL,
    rare integer DEFAULT 0 NOT NULL,
    mythic integer DEFAULT 0 NOT NULL,
    legend integer DEFAULT 0 NOT NULL,
    restock text DEFAULT '0'::text NOT NULL
);


--
-- Name: community; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.community (
    credits bigint DEFAULT 0,
    redeems bigint DEFAULT 0,
    pokes bigint[] DEFAULT ARRAY[]::bigint[],
    items bigint[] DEFAULT ARRAY[]::bigint[],
    other text
);


--
-- Name: current_event; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.current_event (
    u_id bigint NOT NULL,
    event_ranking integer NOT NULL,
    event_title text NOT NULL,
    event_xp integer DEFAULT 0 NOT NULL,
    max_event_xp integer DEFAULT 100 NOT NULL,
    event_level integer DEFAULT 1 NOT NULL,
    rank text NOT NULL
);


--
-- Name: dead_pokes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.dead_pokes (
    id integer NOT NULL,
    pokname text NOT NULL,
    hpiv integer NOT NULL,
    atkiv integer NOT NULL,
    defiv integer NOT NULL,
    spatkiv integer NOT NULL,
    spdefiv integer NOT NULL,
    speediv integer NOT NULL,
    hpev integer NOT NULL,
    atkev integer NOT NULL,
    defev integer NOT NULL,
    spatkev integer NOT NULL,
    spdefev integer NOT NULL,
    speedev integer NOT NULL,
    pokelevel integer NOT NULL,
    moves text[] NOT NULL,
    hitem text NOT NULL,
    exp integer NOT NULL,
    nature text NOT NULL,
    expcap integer NOT NULL,
    poknick text NOT NULL,
    price integer DEFAULT 0 NOT NULL,
    market_enlist boolean DEFAULT false NOT NULL,
    happiness integer DEFAULT 0 NOT NULL,
    fav boolean NOT NULL,
    ability_index integer NOT NULL,
    gender text NOT NULL,
    shiny boolean DEFAULT false,
    counter integer DEFAULT 0,
    name text DEFAULT ''::text,
    time_stamp timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
    caught_by bigint,
    radiant boolean DEFAULT false,
    tags text[] DEFAULT ARRAY[]::text[] NOT NULL,
    skin text,
    tradable boolean DEFAULT true NOT NULL,
    breedable boolean DEFAULT true NOT NULL,
    champion boolean DEFAULT false NOT NULL,
    temp boolean DEFAULT false NOT NULL,
    voucher boolean DEFAULT false,
    owner bigint,
    owned boolean DEFAULT false,
    CONSTRAINT counter_nonnegative CHECK ((counter >= 0)),
    CONSTRAINT evs_check CHECK (((hpev <= 252) AND (atkev <= 252) AND (defev <= 252) AND (spatkev <= 252) AND (spdefev <= 252) AND (speedev <= 252))),
    CONSTRAINT moves_check CHECK ((cardinality(moves) = 4)),
    CONSTRAINT poke_level_check CHECK ((pokelevel <= 100)),
    CONSTRAINT sum_evs_check CHECK (((((((hpev + atkev) + defev) + spatkev) + spdefev) + speedev) <= 510))
);


--
-- Name: dead_radiants; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.dead_radiants (
    pokemon text NOT NULL,
    dead integer,
    id integer NOT NULL,
    types integer[]
);


--
-- Name: dead_radiants_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.dead_radiants_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: dead_radiants_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.dead_radiants_id_seq OWNED BY public.dead_radiants.id;


--
-- Name: disabled_channels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.disabled_channels (
    channel bigint NOT NULL
);


--
-- Name: ditto_donations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ditto_donations (
    u_id bigint,
    amount integer,
    txn_id text,
    date_donated date
);


--
-- Name: dittobitties; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.dittobitties (
    id integer NOT NULL,
    name character varying(255) NOT NULL,
    rarity character varying(20) NOT NULL,
    special boolean DEFAULT false NOT NULL,
    url_1 text NOT NULL,
    url_2 text NOT NULL,
    url_3 text NOT NULL,
    url_4 text NOT NULL,
    url_5 text NOT NULL
);


--
-- Name: dittobitties_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.dittobitties_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: dittobitties_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.dittobitties_id_seq OWNED BY public.dittobitties.id;


--
-- Name: donations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.donations (
    u_id bigint NOT NULL,
    amount integer NOT NULL,
    txn_id text NOT NULL,
    date_donated date DEFAULT CURRENT_DATE
);


--
-- Name: pokes_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.pokes_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: pokes_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.pokes_id_seq OWNED BY public.dead_pokes.id;


--
-- Name: pokes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pokes (
    id integer DEFAULT nextval('public.pokes_id_seq'::regclass) NOT NULL,
    pokname text NOT NULL,
    hpiv integer NOT NULL,
    atkiv integer NOT NULL,
    defiv integer NOT NULL,
    spatkiv integer NOT NULL,
    spdefiv integer NOT NULL,
    speediv integer NOT NULL,
    hpev integer NOT NULL,
    atkev integer NOT NULL,
    defev integer NOT NULL,
    spatkev integer NOT NULL,
    spdefev integer NOT NULL,
    speedev integer NOT NULL,
    pokelevel integer NOT NULL,
    moves text[] NOT NULL,
    hitem text NOT NULL,
    exp integer NOT NULL,
    nature text NOT NULL,
    expcap integer NOT NULL,
    poknick text NOT NULL,
    price integer DEFAULT 0 NOT NULL,
    market_enlist boolean DEFAULT false NOT NULL,
    happiness integer DEFAULT 0 NOT NULL,
    fav boolean NOT NULL,
    ability_index integer NOT NULL,
    gender text NOT NULL,
    shiny boolean DEFAULT false,
    counter integer DEFAULT 0,
    name text DEFAULT ''::text,
    time_stamp timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
    caught_by bigint,
    radiant boolean DEFAULT false,
    tags text[] DEFAULT ARRAY[]::text[] NOT NULL,
    skin text,
    tradable boolean DEFAULT true NOT NULL,
    breedable boolean DEFAULT true NOT NULL,
    champion boolean DEFAULT false NOT NULL,
    temp boolean DEFAULT false NOT NULL,
    voucher boolean DEFAULT false,
    owner bigint,
    owned boolean DEFAULT false,
    ditto_id integer NOT NULL,
    CONSTRAINT counter_nonnegative CHECK ((counter >= 0)),
    CONSTRAINT evs_check CHECK (((hpev <= 252) AND (atkev <= 252) AND (defev <= 252) AND (spatkev <= 252) AND (spdefev <= 252) AND (speedev <= 252))),
    CONSTRAINT moves_check CHECK ((cardinality(moves) = 4)),
    CONSTRAINT poke_level_check CHECK ((pokelevel <= 100)),
    CONSTRAINT sum_evs_check CHECK (((((((hpev + atkev) + defev) + spatkev) + spdefev) + speedev) <= 510))
);


--
-- Name: user_pokemon_ownership; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.user_pokemon_ownership (
    id integer NOT NULL,
    user_id bigint NOT NULL,
    pokemon_id bigint NOT NULL,
    "position" integer NOT NULL,
    is_breeding_female boolean DEFAULT false NOT NULL
);


--
-- Name: duplicate_pokemon_view; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.duplicate_pokemon_view AS
 WITH pokemon_counts AS (
         SELECT user_pokemon_ownership.pokemon_id,
            count(*) AS owner_count
           FROM public.user_pokemon_ownership
          GROUP BY user_pokemon_ownership.pokemon_id
        )
 SELECT pc.pokemon_id,
    pc.owner_count,
    p.pokname AS pokemon_name,
    p.pokelevel AS level,
    p.shiny,
    p.radiant,
    p.owner AS canonical_owner_id
   FROM (pokemon_counts pc
     JOIN public.pokes p ON ((pc.pokemon_id = p.id)))
  WHERE (pc.owner_count > 1);


--
-- Name: egg_hatchery; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.egg_hatchery (
    u_id bigint NOT NULL,
    "1" integer,
    "2" integer,
    "3" integer,
    "4" integer,
    "5" integer,
    "6" integer,
    "7" integer,
    "8" integer,
    "9" integer,
    "10" integer,
    eggs integer[],
    id integer NOT NULL,
    "group" smallint
);


--
-- Name: egg_hatchery_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.egg_hatchery_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: egg_hatchery_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.egg_hatchery_id_seq OWNED BY public.egg_hatchery.id;


--
-- Name: eggs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.eggs (
    u_id bigint NOT NULL,
    bidoof integer DEFAULT 0 NOT NULL,
    caterpie integer DEFAULT 0 NOT NULL,
    pidgey integer DEFAULT 0 NOT NULL,
    magikarp integer DEFAULT 0 NOT NULL,
    spinarak integer DEFAULT 0 NOT NULL,
    tentacruel integer DEFAULT 0 NOT NULL,
    togepi integer DEFAULT 0 NOT NULL,
    bellsprout integer DEFAULT 0 NOT NULL,
    chansey integer DEFAULT 0 NOT NULL,
    omastar integer DEFAULT 0 NOT NULL,
    cubone integer DEFAULT 0 NOT NULL,
    farfetchd integer DEFAULT 0 NOT NULL,
    porygon integer DEFAULT 0 NOT NULL,
    ralts integer DEFAULT 0 NOT NULL,
    dratini integer DEFAULT 0 NOT NULL,
    larvitar integer DEFAULT 0 NOT NULL,
    bagon integer DEFAULT 0 NOT NULL,
    gible integer DEFAULT 0 NOT NULL,
    kyogre integer DEFAULT 0 NOT NULL,
    dialga integer DEFAULT 0 NOT NULL,
    got_radiant boolean DEFAULT false NOT NULL
);


--
-- Name: fraud_networks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.fraud_networks (
    id integer NOT NULL,
    network_id uuid NOT NULL,
    discovered_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    core_user_ids bigint[] NOT NULL,
    network_type character varying(50) NOT NULL,
    estimated_size integer NOT NULL,
    risk_score double precision NOT NULL,
    last_activity timestamp without time zone NOT NULL,
    admin_notes text,
    status character varying(50) DEFAULT 'active'::character varying NOT NULL
);


--
-- Name: fraud_networks_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.fraud_networks_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: fraud_networks_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.fraud_networks_id_seq OWNED BY public.fraud_networks.id;


--
-- Name: gifts; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.gifts (
    gift_id bigint NOT NULL,
    current_owner_id bigint NOT NULL,
    previous_owner_id bigint,
    status character varying(50) NOT NULL,
    transformation_level integer DEFAULT 0,
    is_locked boolean DEFAULT false,
    lock_expiration timestamp without time zone,
    gift_history bigint[],
    creation_timestamp timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
    gift_history_with_time jsonb DEFAULT '[]'::jsonb
);


--
-- Name: gifts_gift_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.gifts_gift_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: gifts_gift_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.gifts_gift_id_seq OWNED BY public.gifts.gift_id;


--
-- Name: gym_log; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.gym_log (
    id bigint NOT NULL,
    gym character varying NOT NULL,
    "time" timestamp without time zone DEFAULT now() NOT NULL,
    challenger bigint NOT NULL,
    leader bigint NOT NULL,
    win boolean NOT NULL
);


--
-- Name: gym_log_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.gym_log_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: gym_log_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.gym_log_id_seq OWNED BY public.gym_log.id;


--
-- Name: gyms; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.gyms (
    u_id bigint NOT NULL,
    theblazingbowl boolean DEFAULT false,
    theunderwaterarena boolean DEFAULT false,
    fieldofflowers boolean DEFAULT false,
    prismpalace boolean DEFAULT false,
    thebasicbungalow boolean DEFAULT false,
    starterstation boolean DEFAULT false,
    floatingisland boolean DEFAULT false,
    stablessstadium boolean DEFAULT false,
    triangulartower boolean DEFAULT false,
    monotypemarket boolean DEFAULT false,
    generationalgallery boolean DEFAULT false,
    bansaucearena boolean DEFAULT false,
    physicalfortress boolean DEFAULT false,
    specialshack boolean DEFAULT false,
    reversedimension boolean DEFAULT false,
    theblazingbowl_ts bigint DEFAULT 0,
    theunderwaterarena_ts bigint DEFAULT 0,
    fieldofflowers_ts bigint DEFAULT 0,
    prismpalace_ts bigint DEFAULT 0,
    thebasicbungalow_ts bigint DEFAULT 0,
    starterstation_ts bigint DEFAULT 0,
    floatingisland_ts bigint DEFAULT 0,
    stablessstadium_ts bigint DEFAULT 0,
    triangulartower_ts bigint DEFAULT 0,
    monotypemarket_ts bigint DEFAULT 0,
    generationalgallery_ts bigint DEFAULT 0,
    bansaucearena_ts bigint DEFAULT 0,
    physicalfortress_ts bigint DEFAULT 0,
    specialshack_ts bigint DEFAULT 0,
    reversedimension_ts bigint DEFAULT 0,
    elite4 boolean DEFAULT false,
    elite4_ts bigint DEFAULT 0,
    cooldown bigint DEFAULT 0
);


--
-- Name: halloween; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.halloween (
    u_id bigint NOT NULL,
    candy integer DEFAULT 0 NOT NULL,
    bone integer DEFAULT 0 NOT NULL,
    pumpkin integer DEFAULT 0 NOT NULL,
    raffle integer DEFAULT 0 NOT NULL,
    username text
);


--
-- Name: hatchery_pokemon; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.hatchery_pokemon (
    "array" integer[]
);


--
-- Name: honey; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.honey (
    channel bigint NOT NULL,
    owner bigint NOT NULL,
    id integer NOT NULL,
    type character varying,
    expires integer DEFAULT 0 NOT NULL
);


--
-- Name: honey_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.honey_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: honey_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.honey_id_seq OWNED BY public.honey.id;


--
-- Name: invalid_pokemon_references; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.invalid_pokemon_references (
    user_id bigint NOT NULL,
    pokemon_id bigint NOT NULL,
    original_position integer NOT NULL,
    recorded_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP
);


--
-- Name: leveling_data; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.leveling_data (
    xp integer,
    level integer,
    title text
);


--
-- Name: market; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.market (
    id bigint NOT NULL,
    poke integer NOT NULL,
    owner bigint NOT NULL,
    price integer NOT NULL,
    buyer bigint,
    listed_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    updated_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    view_count integer DEFAULT 0 NOT NULL,
    CONSTRAINT market_price_positive CHECK ((price > 0)),
    CONSTRAINT market_view_count_positive CHECK ((view_count >= 0))
);


--
-- Name: market_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.market ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.market_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: market_manipulation_events; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.market_manipulation_events (
    id integer NOT NULL,
    detected_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    manipulation_type character varying(50) NOT NULL,
    pokemon_species character varying(100) NOT NULL,
    involved_user_ids bigint[] NOT NULL,
    price_data jsonb NOT NULL,
    confidence_score double precision NOT NULL,
    admin_reviewed boolean DEFAULT false NOT NULL,
    action_taken character varying(100)
);


--
-- Name: market_manipulation_events_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.market_manipulation_events_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: market_manipulation_events_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.market_manipulation_events_id_seq OWNED BY public.market_manipulation_events.id;


--
-- Name: messages; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.messages (
    message_id integer NOT NULL,
    author_id bigint NOT NULL,
    content text NOT NULL,
    "timestamp" timestamp with time zone DEFAULT CURRENT_TIMESTAMP,
    is_system_message boolean DEFAULT false,
    is_read boolean DEFAULT false,
    title text,
    inbox bigint
);


--
-- Name: messages_message_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.messages_message_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: messages_message_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.messages_message_id_seq OWNED BY public.messages.message_id;


--
-- Name: milestone_progress; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.milestone_progress (
    u_id bigint NOT NULL,
    achievement_type character varying(50) NOT NULL,
    milestone_value integer NOT NULL,
    completed_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP
);


--
-- Name: mothers; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mothers (
    pokemon_id bigint NOT NULL,
    owner bigint,
    entry_time timestamp without time zone DEFAULT now()
);


--
-- Name: trade_logs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.trade_logs (
    sender_pokes bigint[] DEFAULT ARRAY[]::bigint[],
    sender_credits bigint DEFAULT 0,
    sender bigint NOT NULL,
    receiver_pokes bigint[] DEFAULT ARRAY[]::bigint[],
    receiver_credits bigint DEFAULT 0,
    receiver bigint NOT NULL,
    command text NOT NULL,
    t_id integer NOT NULL,
    "time" timestamp with time zone,
    sender_redeems bigint DEFAULT 0 NOT NULL,
    receiver_redeems bigint DEFAULT 0 NOT NULL
);


--
-- Name: mv_trading_network_graph; Type: MATERIALIZED VIEW; Schema: public; Owner: -
--

CREATE MATERIALIZED VIEW public.mv_trading_network_graph AS
 WITH trade_intervals AS (
         SELECT LEAST(trade_logs.sender, trade_logs.receiver) AS user1,
            GREATEST(trade_logs.sender, trade_logs.receiver) AS user2,
            trade_logs."time",
            lag(trade_logs."time") OVER (PARTITION BY LEAST(trade_logs.sender, trade_logs.receiver), GREATEST(trade_logs.sender, trade_logs.receiver) ORDER BY trade_logs."time") AS prev_time
           FROM public.trade_logs
          WHERE (trade_logs."time" >= (CURRENT_TIMESTAMP - '30 days'::interval))
        ), recent_trades AS (
         SELECT trade_intervals.user1,
            trade_intervals.user2,
            count(*) AS trade_count,
            max(trade_intervals."time") AS last_trade,
            avg(
                CASE
                    WHEN (trade_intervals.prev_time IS NOT NULL) THEN EXTRACT(epoch FROM (trade_intervals."time" - trade_intervals.prev_time))
                    ELSE NULL::numeric
                END) AS avg_interval
           FROM trade_intervals
          GROUP BY trade_intervals.user1, trade_intervals.user2
         HAVING (count(*) >= 3)
        )
 SELECT user1,
    user2,
    trade_count,
    last_trade,
    avg_interval,
        CASE
            WHEN ((trade_count >= 10) AND (avg_interval < (3600)::numeric)) THEN 'high'::text
            WHEN ((trade_count >= 5) AND (avg_interval < (7200)::numeric)) THEN 'medium'::text
            ELSE 'low'::text
        END AS connection_strength
   FROM recent_trades
  WITH NO DATA;


--
-- Name: new_users; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.new_users (
    u_id bigint NOT NULL,
    completed boolean DEFAULT false,
    task1 jsonb DEFAULT '{"bag": false, "bal": false, "hunt": false, "region": false, "trainernick": false}'::jsonb,
    task2 jsonb DEFAULT '{"info": false, "vote": false, "invite": false, "updates": false, "leaderboard": false}'::jsonb,
    task3 jsonb DEFAULT '{"server": false, "spawns": false, "silence": false, "redirect": false, "botcommands": false}'::jsonb,
    task4 jsonb DEFAULT '{"/f": false, "/p": false, "/fav": {"add": false, "list": false, "remove": false}, "/tags": {"add": false, "list": false, "remove": false}, "/order": {"evs": false, "ivs": false, "name": false, "level": false, "default": false}, "/party": {"add": false, "list": false, "load": false, "view": false, "setup": false, "remove": false, "register": false, "deregister": false}, "/select": false, "/pokedex": false}'::jsonb,
    task5 jsonb DEFAULT '{"/duel": {"npc": false, "party": false, "single": false, "inverse": false}, "/fish": false, "/breed": false, "/missions": false}'::jsonb,
    task6 jsonb DEFAULT '{"/m": {"market_add": false, "market_buy": false, "market_info": false, "market_remove": false}, "/buy": {"item": false, "candy": false, "chest": false, "daycare": false, "redeems": false, "vitamins": false}, "/gift": {"credits": false, "pokemon": false, "redeems": false}, "/sell": {"egg": false, "item": false}, "/shop": false, "/trade": false}'::jsonb,
    alltasks boolean DEFAULT false,
    opt_out boolean DEFAULT false
);


--
-- Name: p_stats; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.p_stats (
    pokemon character varying(255) NOT NULL,
    wins integer,
    faints integer
);


--
-- Name: partys; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.partys (
    slot1 bigint,
    slot2 bigint,
    slot3 bigint,
    slot4 bigint,
    slot5 bigint,
    slot6 bigint,
    p_id integer NOT NULL,
    u_id bigint NOT NULL,
    name text,
    quick boolean DEFAULT false NOT NULL,
    is_current_party boolean DEFAULT false NOT NULL
);


--
-- Name: partys_p_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.partys_p_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: partys_p_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.partys_p_id_seq OWNED BY public.partys.p_id;


--
-- Name: patreon_store; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.patreon_store (
    u_id bigint NOT NULL,
    reset bigint NOT NULL
);


--
-- Name: patreonstore; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.patreonstore (
    lastdate text NOT NULL,
    u_id bigint NOT NULL
);


--
-- Name: poke_totals; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.poke_totals AS
 SELECT pokname,
    count(id) AS count
   FROM public.dead_pokes
  GROUP BY pokname;


--
-- Name: pokemon_laundering_history; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pokemon_laundering_history (
    id integer NOT NULL,
    pokemon_id bigint NOT NULL,
    analysis_timestamp timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    ownership_chain_length integer NOT NULL,
    rapid_transfer_count integer NOT NULL,
    circular_path boolean DEFAULT false NOT NULL,
    estimated_value numeric(20,2),
    risk_score double precision NOT NULL,
    flagged boolean DEFAULT false NOT NULL
);


--
-- Name: pokemon_laundering_history_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.pokemon_laundering_history_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: pokemon_laundering_history_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.pokemon_laundering_history_id_seq OWNED BY public.pokemon_laundering_history.id;


--
-- Name: pokemon_totals; Type: MATERIALIZED VIEW; Schema: public; Owner: -
--

CREATE MATERIALIZED VIEW public.pokemon_totals AS
 SELECT pokname,
    count
   FROM public.poke_totals
  WITH NO DATA;


--
-- Name: pokes_ditto_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.pokes_ditto_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: pokes_ditto_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.pokes_ditto_id_seq OWNED BY public.pokes.ditto_id;


--
-- Name: redeemstore; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.redeemstore (
    u_id bigint NOT NULL,
    bought integer DEFAULT 0 NOT NULL,
    restock text DEFAULT '0'::text NOT NULL
);



--
-- Name: servers; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.servers (
    serverid bigint NOT NULL,
    prefix text DEFAULT ';'::text NOT NULL,
    language character varying(2),
    delspawns boolean DEFAULT false,
    pinspawns boolean DEFAULT false,
    redirects numeric(20,0)[],
    disabled_channels numeric(20,0)[],
    spawns_disabled numeric(20,0)[]
);


--
-- Name: skylog; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.skylog (
    id integer NOT NULL,
    u_id bigint NOT NULL,
    command text NOT NULL,
    args text NOT NULL,
    jump text,
    "time" timestamp without time zone NOT NULL,
    note character varying
);


--
-- Name: skylog_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.skylog_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: skylog_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.skylog_id_seq OWNED BY public.skylog.id;


--
-- Name: suspicious_trade_analytics; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.suspicious_trade_analytics (
    id integer NOT NULL,
    trade_id integer NOT NULL,
    analysis_timestamp timestamp without time zone NOT NULL,
    overall_risk_score double precision NOT NULL,
    value_imbalance_score double precision NOT NULL,
    relationship_risk_score double precision NOT NULL,
    behavioral_risk_score double precision NOT NULL,
    account_age_risk_score double precision NOT NULL,
    sender_total_value numeric(20,2) NOT NULL,
    receiver_total_value numeric(20,2) NOT NULL,
    value_ratio double precision NOT NULL,
    value_difference numeric(20,2) NOT NULL,
    sender_account_age_days integer NOT NULL,
    receiver_account_age_days integer NOT NULL,
    previous_trades_count integer NOT NULL,
    previous_total_value numeric(20,2) NOT NULL,
    flagged_alt_account boolean DEFAULT false NOT NULL,
    flagged_rmt boolean DEFAULT false NOT NULL,
    flagged_newbie_exploitation boolean DEFAULT false NOT NULL,
    flagged_unusual_behavior boolean DEFAULT false NOT NULL,
    flagged_bot_activity boolean DEFAULT false NOT NULL,
    analysis_notes text,
    admin_reviewed boolean DEFAULT false NOT NULL,
    admin_verdict boolean,
    admin_notes text,
    admin_review_timestamp timestamp without time zone
);


--
-- Name: suspicious_trade_analytics_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.suspicious_trade_analytics_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: suspicious_trade_analytics_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.suspicious_trade_analytics_id_seq OWNED BY public.suspicious_trade_analytics.id;


--
-- Name: tokens; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.tokens (
    token character varying NOT NULL,
    u_id character varying NOT NULL,
    username character varying NOT NULL,
    discriminator character varying,
    avatar character varying,
    refresh_token character varying
);


--
-- Name: tourny_teams; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.tourny_teams (
    id integer NOT NULL,
    team character varying[] DEFAULT '{}'::character varying[] NOT NULL,
    u_id bigint NOT NULL,
    staff bigint NOT NULL,
    team_ids integer[]
);


--
-- Name: tourny_teams_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.tourny_teams_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: tourny_teams_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.tourny_teams_id_seq OWNED BY public.tourny_teams.id;


--
-- Name: trade_fraud_detections; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.trade_fraud_detections (
    id integer NOT NULL,
    trade_id integer,
    detection_timestamp timestamp without time zone NOT NULL,
    primary_user_id bigint NOT NULL,
    secondary_user_id bigint,
    additional_user_ids text,
    fraud_type integer NOT NULL,
    confidence_level double precision NOT NULL,
    risk_score double precision NOT NULL,
    triggered_rules text NOT NULL,
    detection_details text,
    automated_action integer NOT NULL,
    trade_blocked boolean DEFAULT false NOT NULL,
    users_notified boolean DEFAULT false NOT NULL,
    admin_alerted boolean DEFAULT false NOT NULL,
    investigation_status integer DEFAULT 1 NOT NULL,
    assigned_admin_id bigint,
    investigation_started timestamp without time zone,
    resolution_timestamp timestamp without time zone,
    final_verdict integer,
    admin_notes text,
    admin_actions text,
    false_positive boolean DEFAULT false NOT NULL,
    false_positive_reason text,
    requires_rule_adjustment boolean DEFAULT false NOT NULL,
    suspicious_trade_analytics_id integer,
    "TradeLogTradeId" integer,
    chain_trading_detected boolean DEFAULT false NOT NULL,
    burst_trading_detected boolean DEFAULT false NOT NULL,
    network_fraud_detected boolean DEFAULT false NOT NULL,
    market_manipulation_detected boolean DEFAULT false NOT NULL,
    pokemon_laundering_detected boolean DEFAULT false NOT NULL,
    comprehensive_risk_score double precision,
    actionable_insights text
);


--
-- Name: trade_fraud_detections_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.trade_fraud_detections_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: trade_fraud_detections_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.trade_fraud_detections_id_seq OWNED BY public.trade_fraud_detections.id;


--
-- Name: trade_logs_t_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.trade_logs_t_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: trade_logs_t_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.trade_logs_t_id_seq OWNED BY public.trade_logs.t_id;


--
-- Name: updates; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.updates (
    id integer NOT NULL,
    update text NOT NULL,
    update_date date DEFAULT CURRENT_DATE NOT NULL,
    dev text
);


--
-- Name: updates_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.updates_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: updates_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.updates_id_seq OWNED BY public.updates.id;


--
-- Name: user_dittobitties; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.user_dittobitties (
    id integer NOT NULL,
    user_id bigint NOT NULL,
    dbitty_id integer NOT NULL,
    str integer NOT NULL,
    "int" integer NOT NULL,
    pdef integer NOT NULL,
    mdef integer NOT NULL,
    agi integer NOT NULL,
    con integer NOT NULL,
    experience integer DEFAULT 0 NOT NULL,
    stage smallint DEFAULT 1 NOT NULL,
    current_hp integer DEFAULT 10 NOT NULL
);


--
-- Name: user_dittobitties_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.user_dittobitties_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: user_dittobitties_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.user_dittobitties_id_seq OWNED BY public.user_dittobitties.id;


--
-- Name: user_filter_criteria; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.user_filter_criteria (
    id integer NOT NULL,
    filter_group_id integer NOT NULL,
    field_name character varying(50) NOT NULL,
    operator character varying(20) NOT NULL,
    value_text character varying(255),
    value_numeric integer,
    value_numeric_max integer,
    value_boolean boolean,
    logical_connector character varying(5),
    criterion_order integer DEFAULT 0 NOT NULL
);


--
-- Name: user_filter_criteria_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.user_filter_criteria_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: user_filter_criteria_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.user_filter_criteria_id_seq OWNED BY public.user_filter_criteria.id;


--
-- Name: user_filter_groups; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.user_filter_groups (
    id integer NOT NULL,
    user_id bigint NOT NULL,
    name character varying(100) NOT NULL,
    description character varying(500),
    color character varying(7) DEFAULT '#3B82F6'::character varying NOT NULL,
    icon character varying(50) DEFAULT '📁'::character varying NOT NULL,
    is_favorite boolean DEFAULT false NOT NULL,
    sort_order integer DEFAULT 0 NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: user_filter_groups_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.user_filter_groups_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: user_filter_groups_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.user_filter_groups_id_seq OWNED BY public.user_filter_groups.id;


--
-- Name: user_loyalty; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.user_loyalty (
    u_id bigint NOT NULL,
    loyalty_points integer DEFAULT 0,
    daily_streak integer DEFAULT 0,
    last_login date,
    created_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamp without time zone DEFAULT CURRENT_TIMESTAMP
);


--
-- Name: user_pokemon_ownership_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.user_pokemon_ownership_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: user_pokemon_ownership_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.user_pokemon_ownership_id_seq OWNED BY public.user_pokemon_ownership.id;


--
-- Name: user_pokemon_view; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.user_pokemon_view AS
 SELECT upo.id AS ownership_id,
    upo.user_id,
    upo.pokemon_id,
    upo."position",
    p.pokname AS pokemon_name,
    p.pokelevel AS level,
    p.owner AS canonical_owner,
        CASE
            WHEN (p.owner = upo.user_id) THEN true
            ELSE false
        END AS is_canonical_owner
   FROM (public.user_pokemon_ownership upo
     JOIN public.pokes p ON ((upo.pokemon_id = p.id)));


--
-- Name: user_risk_profiles; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.user_risk_profiles (
    user_id bigint NOT NULL,
    last_analyzed timestamp without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    total_trades_analyzed integer DEFAULT 0 NOT NULL,
    average_risk_score double precision DEFAULT 0 NOT NULL,
    high_risk_trade_count integer DEFAULT 0 NOT NULL,
    chain_trading_incidents integer DEFAULT 0 NOT NULL,
    burst_trading_incidents integer DEFAULT 0 NOT NULL,
    market_manipulation_incidents integer DEFAULT 0 NOT NULL,
    risk_score_history double precision[] DEFAULT '{}'::double precision[],
    profile_data jsonb
);


--
-- Name: user_trade_relationships; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.user_trade_relationships (
    id integer NOT NULL,
    user1_id bigint NOT NULL,
    user2_id bigint NOT NULL,
    total_trades integer DEFAULT 0 NOT NULL,
    first_trade_timestamp timestamp without time zone NOT NULL,
    last_trade_timestamp timestamp without time zone NOT NULL,
    user1_total_given_value numeric(20,2) DEFAULT 0 NOT NULL,
    user2_total_given_value numeric(20,2) DEFAULT 0 NOT NULL,
    user1_favoring_trades integer DEFAULT 0 NOT NULL,
    user2_favoring_trades integer DEFAULT 0 NOT NULL,
    balanced_trades integer DEFAULT 0 NOT NULL,
    relationship_risk_score double precision DEFAULT 0 NOT NULL,
    value_imbalance_ratio double precision DEFAULT 1 NOT NULL,
    trading_frequency_score double precision DEFAULT 0 NOT NULL,
    flagged_potential_alts boolean DEFAULT false NOT NULL,
    flagged_potential_rmt boolean DEFAULT false NOT NULL,
    flagged_newbie_exploitation boolean DEFAULT false NOT NULL,
    account_age_difference_days integer DEFAULT 0 NOT NULL,
    suspicious_creation_timing boolean DEFAULT false NOT NULL,
    newer_account_age_at_first_trade integer DEFAULT 0 CONSTRAINT user_trade_relationships_newer_account_age_at_first_tr_not_null NOT NULL,
    average_trade_interval_hours double precision DEFAULT 0 NOT NULL,
    trade_interval_std_dev double precision DEFAULT 0 NOT NULL,
    suspicious_timing_pattern boolean DEFAULT false NOT NULL,
    last_updated timestamp without time zone NOT NULL,
    admin_reviewed boolean DEFAULT false NOT NULL,
    admin_verdict boolean,
    admin_notes text,
    whitelisted boolean DEFAULT false NOT NULL,
    chain_trading_score double precision DEFAULT 0,
    burst_trading_incidents integer DEFAULT 0,
    network_connection_strength double precision DEFAULT 0,
    last_chain_analysis timestamp without time zone,
    last_burst_analysis timestamp without time zone,
    CONSTRAINT check_user_order CHECK ((user1_id < user2_id))
);


--
-- Name: user_trade_relationships_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.user_trade_relationships_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: user_trade_relationships_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.user_trade_relationships_id_seq OWNED BY public.user_trade_relationships.id;


--
-- Name: users_active; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.users_active (
    u_id bigint,
    redeems integer,
    evpoints integer,
    tnick text,
    upvotepoints integer,
    mewcoins bigint,
    user_order text,
    pokes integer[],
    inventory jsonb,
    items jsonb,
    daycare integer,
    daycarelimit integer,
    energy integer,
    held_item text,
    fishing_exp bigint,
    fishing_level integer,
    energy_hour integer,
    fishing_level_cap bigint,
    party integer[],
    luck integer,
    npc_relationships jsonb,
    selected integer,
    visible boolean,
    silenced boolean,
    tradelock boolean,
    voted boolean,
    id integer,
    restored boolean,
    bike boolean,
    botbanned boolean,
    marketlimit integer,
    staff text,
    gym_leader boolean,
    oxiconverted boolean,
    event integer,
    last_vote integer,
    vote_streak integer,
    skins jsonb,
    holidayinv jsonb,
    raffle integer,
    region text,
    hunt text,
    chain integer,
    patreon_override character varying,
    note character varying,
    skin_tokens integer,
    comp boolean,
    show_donations boolean,
    mystery_token integer,
    event_skin_tokens integer,
    os_rep integer,
    wombo_ticket integer,
    active boolean
);


--
-- Name: users_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.users_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: users_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.users_id_seq OWNED BY public.users.id;


--
-- Name: users_inactive; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.users_inactive (
    u_id bigint,
    redeems integer,
    evpoints integer,
    tnick text,
    upvotepoints integer,
    mewcoins bigint,
    user_order text,
    pokes integer[],
    inventory jsonb,
    items jsonb,
    daycare integer,
    daycarelimit integer,
    energy integer,
    held_item text,
    fishing_exp bigint,
    fishing_level integer,
    energy_hour integer,
    fishing_level_cap bigint,
    party integer[],
    luck integer,
    npc_relationships jsonb,
    selected integer,
    visible boolean,
    silenced boolean,
    tradelock boolean,
    voted boolean,
    id integer,
    restored boolean,
    bike boolean,
    botbanned boolean,
    marketlimit integer,
    staff text,
    gym_leader boolean,
    oxiconverted boolean,
    event integer,
    last_vote integer,
    vote_streak integer,
    skins jsonb,
    holidayinv jsonb,
    raffle integer,
    region text,
    hunt text,
    chain integer,
    patreon_override character varying,
    note character varying,
    skin_tokens integer,
    comp boolean,
    show_donations boolean,
    mystery_token integer,
    event_skin_tokens integer,
    os_rep integer,
    wombo_ticket integer,
    active boolean
);


--
-- Name: voucher_requests; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.voucher_requests (
    m_id bigint NOT NULL,
    u_id bigint NOT NULL,
    status text[],
    artist bigint
);


--
-- Name: UserFilterCriteria Id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserFilterCriteria" ALTER COLUMN "Id" SET DEFAULT nextval('public."UserFilterCriteria_Id_seq"'::regclass);


--
-- Name: UserFilterGroups Id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserFilterGroups" ALTER COLUMN "Id" SET DEFAULT nextval('public."UserFilterGroups_Id_seq"'::regclass);


--
-- Name: ai_generations id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ai_generations ALTER COLUMN id SET DEFAULT nextval('public.ai_generations_id_seq'::regclass);


--
-- Name: ai_themes id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ai_themes ALTER COLUMN id SET DEFAULT nextval('public.ai_themes_id_seq'::regclass);


--
-- Name: announce id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.announce ALTER COLUMN id SET DEFAULT nextval('public.announce_id_seq'::regclass);


--
-- Name: artists id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists ALTER COLUMN id SET DEFAULT nextval('public.artists_id_seq'::regclass);


--
-- Name: artists_consent id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists_consent ALTER COLUMN id SET DEFAULT nextval('public.artists_consent_id_seq'::regclass);


--
-- Name: burst_trading_patterns id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.burst_trading_patterns ALTER COLUMN id SET DEFAULT nextval('public.burst_trading_patterns_id_seq'::regclass);


--
-- Name: cal id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.cal ALTER COLUMN id SET DEFAULT nextval('public.cal_id_seq'::regclass);


--
-- Name: dead_pokes id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dead_pokes ALTER COLUMN id SET DEFAULT nextval('public.pokes_id_seq'::regclass);


--
-- Name: dead_radiants id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dead_radiants ALTER COLUMN id SET DEFAULT nextval('public.dead_radiants_id_seq'::regclass);


--
-- Name: dittobitties id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dittobitties ALTER COLUMN id SET DEFAULT nextval('public.dittobitties_id_seq'::regclass);


--
-- Name: egg_hatchery id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery ALTER COLUMN id SET DEFAULT nextval('public.egg_hatchery_id_seq'::regclass);


--
-- Name: fraud_networks id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fraud_networks ALTER COLUMN id SET DEFAULT nextval('public.fraud_networks_id_seq'::regclass);


--
-- Name: gifts gift_id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.gifts ALTER COLUMN gift_id SET DEFAULT nextval('public.gifts_gift_id_seq'::regclass);


--
-- Name: gym_log id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.gym_log ALTER COLUMN id SET DEFAULT nextval('public.gym_log_id_seq'::regclass);


--
-- Name: honey id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.honey ALTER COLUMN id SET DEFAULT nextval('public.honey_id_seq'::regclass);


--
-- Name: market_manipulation_events id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.market_manipulation_events ALTER COLUMN id SET DEFAULT nextval('public.market_manipulation_events_id_seq'::regclass);


--
-- Name: messages message_id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.messages ALTER COLUMN message_id SET DEFAULT nextval('public.messages_message_id_seq'::regclass);


--
-- Name: partys p_id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.partys ALTER COLUMN p_id SET DEFAULT nextval('public.partys_p_id_seq'::regclass);


--
-- Name: pokemon_laundering_history id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pokemon_laundering_history ALTER COLUMN id SET DEFAULT nextval('public.pokemon_laundering_history_id_seq'::regclass);


--
-- Name: pokes ditto_id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pokes ALTER COLUMN ditto_id SET DEFAULT nextval('public.pokes_ditto_id_seq'::regclass);


--
-- Name: skylog id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.skylog ALTER COLUMN id SET DEFAULT nextval('public.skylog_id_seq'::regclass);


--
-- Name: suspicious_trade_analytics id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.suspicious_trade_analytics ALTER COLUMN id SET DEFAULT nextval('public.suspicious_trade_analytics_id_seq'::regclass);


--
-- Name: tourny_teams id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tourny_teams ALTER COLUMN id SET DEFAULT nextval('public.tourny_teams_id_seq'::regclass);


--
-- Name: trade_fraud_detections id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trade_fraud_detections ALTER COLUMN id SET DEFAULT nextval('public.trade_fraud_detections_id_seq'::regclass);


--
-- Name: trade_logs t_id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trade_logs ALTER COLUMN t_id SET DEFAULT nextval('public.trade_logs_t_id_seq'::regclass);


--
-- Name: updates id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.updates ALTER COLUMN id SET DEFAULT nextval('public.updates_id_seq'::regclass);


--
-- Name: user_dittobitties id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_dittobitties ALTER COLUMN id SET DEFAULT nextval('public.user_dittobitties_id_seq'::regclass);


--
-- Name: user_filter_criteria id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_filter_criteria ALTER COLUMN id SET DEFAULT nextval('public.user_filter_criteria_id_seq'::regclass);


--
-- Name: user_filter_groups id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_filter_groups ALTER COLUMN id SET DEFAULT nextval('public.user_filter_groups_id_seq'::regclass);


--
-- Name: user_pokemon_ownership id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_pokemon_ownership ALTER COLUMN id SET DEFAULT nextval('public.user_pokemon_ownership_id_seq'::regclass);


--
-- Name: user_trade_relationships id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_trade_relationships ALTER COLUMN id SET DEFAULT nextval('public.user_trade_relationships_id_seq'::regclass);


--
-- Name: users id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users ALTER COLUMN id SET DEFAULT nextval('public.users_id_seq'::regclass);


--
-- Name: ActiveSpawns ActiveSpawns_MessageId_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ActiveSpawns"
    ADD CONSTRAINT "ActiveSpawns_MessageId_key" UNIQUE ("MessageId");


--
-- Name: ActiveSpawns ActiveSpawns_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ActiveSpawns"
    ADD CONSTRAINT "ActiveSpawns_pkey" PRIMARY KEY ("MessageId");


--
-- Name: UserFilterCriteria UserFilterCriteria_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserFilterCriteria"
    ADD CONSTRAINT "UserFilterCriteria_pkey" PRIMARY KEY ("Id");


--
-- Name: UserFilterGroups UserFilterGroups_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserFilterGroups"
    ADD CONSTRAINT "UserFilterGroups_pkey" PRIMARY KEY ("Id");


--
-- Name: achievements achievements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.achievements
    ADD CONSTRAINT achievements_pkey PRIMARY KEY (u_id);


--
-- Name: ai_generations ai_generations_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ai_generations
    ADD CONSTRAINT ai_generations_pkey PRIMARY KEY (id, theme);


--
-- Name: ai_themes ai_themes_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ai_themes
    ADD CONSTRAINT ai_themes_pkey PRIMARY KEY (id, name);


--
-- Name: announce announce_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.announce
    ADD CONSTRAINT announce_pkey PRIMARY KEY (id);


--
-- Name: artists_consent artists_consent_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists_consent
    ADD CONSTRAINT artists_consent_pkey PRIMARY KEY (artist);


--
-- Name: artists artists_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists
    ADD CONSTRAINT artists_pkey PRIMARY KEY (id);


--
-- Name: burst_trading_patterns burst_trading_patterns_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.burst_trading_patterns
    ADD CONSTRAINT burst_trading_patterns_pkey PRIMARY KEY (id);


--
-- Name: cal cal_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.cal
    ADD CONSTRAINT cal_pkey PRIMARY KEY (id);


--
-- Name: cheststore cheststore_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.cheststore
    ADD CONSTRAINT cheststore_pkey PRIMARY KEY (u_id);


--
-- Name: current_event current_event_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.current_event
    ADD CONSTRAINT current_event_pkey PRIMARY KEY (u_id);


--
-- Name: dead_radiants dead_radiants_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dead_radiants
    ADD CONSTRAINT dead_radiants_id_key UNIQUE (id);


--
-- Name: dead_radiants dead_radiants_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dead_radiants
    ADD CONSTRAINT dead_radiants_pkey PRIMARY KEY (pokemon);


--
-- Name: dittobitties dittobitties_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dittobitties
    ADD CONSTRAINT dittobitties_pkey PRIMARY KEY (id);


--
-- Name: egg_hatchery egg_hatchery_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT egg_hatchery_pkey PRIMARY KEY (id);


--
-- Name: eggs eggs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.eggs
    ADD CONSTRAINT eggs_pkey PRIMARY KEY (u_id);


--
-- Name: fraud_networks fraud_networks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fraud_networks
    ADD CONSTRAINT fraud_networks_pkey PRIMARY KEY (id);


--
-- Name: gifts gifts_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.gifts
    ADD CONSTRAINT gifts_pkey PRIMARY KEY (gift_id);


--
-- Name: gym_log gym_log_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.gym_log
    ADD CONSTRAINT gym_log_pkey PRIMARY KEY (id);


--
-- Name: gyms gyms_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.gyms
    ADD CONSTRAINT gyms_pkey PRIMARY KEY (u_id);


--
-- Name: halloween halloween_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.halloween
    ADD CONSTRAINT halloween_pkey PRIMARY KEY (u_id);


--
-- Name: honey honey_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.honey
    ADD CONSTRAINT honey_pkey PRIMARY KEY (id);


--
-- Name: market_manipulation_events market_manipulation_events_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.market_manipulation_events
    ADD CONSTRAINT market_manipulation_events_pkey PRIMARY KEY (id);


--
-- Name: market market_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.market
    ADD CONSTRAINT market_pkey PRIMARY KEY (id);


--
-- Name: messages messages_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.messages
    ADD CONSTRAINT messages_pkey PRIMARY KEY (message_id);


--
-- Name: milestone_progress milestone_progress_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.milestone_progress
    ADD CONSTRAINT milestone_progress_pkey PRIMARY KEY (u_id, achievement_type, milestone_value);


--
-- Name: pokes new3_pokes_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pokes
    ADD CONSTRAINT new3_pokes_pkey PRIMARY KEY (id);


--
-- Name: new_users new_users_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.new_users
    ADD CONSTRAINT new_users_pkey PRIMARY KEY (u_id);


--
-- Name: p_stats p_stats_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.p_stats
    ADD CONSTRAINT p_stats_pkey PRIMARY KEY (pokemon);


--
-- Name: partys partys_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.partys
    ADD CONSTRAINT partys_pkey PRIMARY KEY (p_id);


--
-- Name: patreon_store patreon_store_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patreon_store
    ADD CONSTRAINT patreon_store_pkey PRIMARY KEY (u_id);


--
-- Name: pokemon_laundering_history pokemon_laundering_history_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pokemon_laundering_history
    ADD CONSTRAINT pokemon_laundering_history_pkey PRIMARY KEY (id);


--
-- Name: dead_pokes pokes_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dead_pokes
    ADD CONSTRAINT pokes_pkey PRIMARY KEY (id);


--
-- Name: redeemstore redeemstore_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.redeemstore
    ADD CONSTRAINT redeemstore_pkey PRIMARY KEY (u_id);


--
-- Name: skylog skylog_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.skylog
    ADD CONSTRAINT skylog_pkey PRIMARY KEY (id);


--
-- Name: suspicious_trade_analytics suspicious_trade_analytics_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.suspicious_trade_analytics
    ADD CONSTRAINT suspicious_trade_analytics_pkey PRIMARY KEY (id);


--
-- Name: tourny_teams tourny_teams_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tourny_teams
    ADD CONSTRAINT tourny_teams_pkey PRIMARY KEY (u_id);


--
-- Name: trade_fraud_detections trade_fraud_detections_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trade_fraud_detections
    ADD CONSTRAINT trade_fraud_detections_pkey PRIMARY KEY (id);


--
-- Name: trade_logs trade_logs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trade_logs
    ADD CONSTRAINT trade_logs_pkey PRIMARY KEY (t_id);


--
-- Name: users u_id_check; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT u_id_check UNIQUE (u_id);


--
-- Name: user_trade_relationships unique_user_pair; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_trade_relationships
    ADD CONSTRAINT unique_user_pair UNIQUE (user1_id, user2_id);


--
-- Name: updates updates_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.updates
    ADD CONSTRAINT updates_pkey PRIMARY KEY (id);


--
-- Name: user_dittobitties user_dittobitties_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_dittobitties
    ADD CONSTRAINT user_dittobitties_pkey PRIMARY KEY (id);


--
-- Name: user_filter_criteria user_filter_criteria_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_filter_criteria
    ADD CONSTRAINT user_filter_criteria_pkey PRIMARY KEY (id);


--
-- Name: user_filter_groups user_filter_groups_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_filter_groups
    ADD CONSTRAINT user_filter_groups_pkey PRIMARY KEY (id);


--
-- Name: user_loyalty user_loyalty_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_loyalty
    ADD CONSTRAINT user_loyalty_pkey PRIMARY KEY (u_id);


--
-- Name: user_pokemon_ownership user_pokemon_ownership_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_pokemon_ownership
    ADD CONSTRAINT user_pokemon_ownership_pkey PRIMARY KEY (id);


--
-- Name: user_risk_profiles user_risk_profiles_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_risk_profiles
    ADD CONSTRAINT user_risk_profiles_pkey PRIMARY KEY (user_id);


--
-- Name: user_trade_relationships user_trade_relationships_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_trade_relationships
    ADD CONSTRAINT user_trade_relationships_pkey PRIMARY KEY (id);


--
-- Name: users username; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT username UNIQUE (tnick);


--
-- Name: voucher_requests voucher_requests_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.voucher_requests
    ADD CONSTRAINT voucher_requests_pkey PRIMARY KEY (m_id);


--
-- Name: IX_ActiveSpawns_ChannelId_IsCaught; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ActiveSpawns_ChannelId_IsCaught" ON public."ActiveSpawns" USING btree ("ChannelId", "IsCaught");


--
-- Name: IX_ActiveSpawns_CreatedAt; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ActiveSpawns_CreatedAt" ON public."ActiveSpawns" USING btree ("CreatedAt");


--
-- Name: IX_ActiveSpawns_MessageId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ActiveSpawns_MessageId" ON public."ActiveSpawns" USING btree ("MessageId");


--
-- Name: IX_UserFilterCriteria_CriterionOrder; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_UserFilterCriteria_CriterionOrder" ON public."UserFilterCriteria" USING btree ("CriterionOrder");


--
-- Name: IX_UserFilterCriteria_FilterGroupId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_UserFilterCriteria_FilterGroupId" ON public."UserFilterCriteria" USING btree ("FilterGroupId");


--
-- Name: IX_UserFilterGroups_SortOrder; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_UserFilterGroups_SortOrder" ON public."UserFilterGroups" USING btree ("SortOrder");


--
-- Name: IX_UserFilterGroups_UserId_IsActive; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_UserFilterGroups_UserId_IsActive" ON public."UserFilterGroups" USING btree ("UserId", "IsActive");


--
-- Name: IX_UserFilterGroups_UserId_Name; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_UserFilterGroups_UserId_Name" ON public."UserFilterGroups" USING btree ("UserId", "Name");


--
-- Name: IX_UserFilterGroups_UserId_Name_Unique; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_UserFilterGroups_UserId_Name_Unique" ON public."UserFilterGroups" USING btree ("UserId", "Name") WHERE ("IsActive" = true);


--
-- Name: IX_user_filter_criteria_criterion_order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_user_filter_criteria_criterion_order" ON public.user_filter_criteria USING btree (criterion_order);


--
-- Name: IX_user_filter_criteria_filter_group_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_user_filter_criteria_filter_group_id" ON public.user_filter_criteria USING btree (filter_group_id);


--
-- Name: IX_user_filter_groups_sort_order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_user_filter_groups_sort_order" ON public.user_filter_groups USING btree (sort_order);


--
-- Name: IX_user_filter_groups_user_id_is_active; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_user_filter_groups_user_id_is_active" ON public.user_filter_groups USING btree (user_id, is_active);


--
-- Name: IX_user_filter_groups_user_id_name; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_user_filter_groups_user_id_name" ON public.user_filter_groups USING btree (user_id, name);


--
-- Name: IX_user_filter_groups_user_id_name_unique; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_user_filter_groups_user_id_name_unique" ON public.user_filter_groups USING btree (user_id, name) WHERE (is_active = true);


--
-- Name: idx_burst_trading_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_burst_trading_time ON public.burst_trading_patterns USING btree (detected_at);


--
-- Name: idx_burst_trading_user; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_burst_trading_user ON public.burst_trading_patterns USING btree (user_id);


--
-- Name: idx_fraud_networks_core_users; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_fraud_networks_core_users ON public.fraud_networks USING gin (core_user_ids);


--
-- Name: idx_fraud_networks_network_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_fraud_networks_network_id ON public.fraud_networks USING btree (network_id);


--
-- Name: idx_market_active_listings; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_active_listings ON public.market USING btree (listed_at DESC) WHERE (buyer IS NULL);


--
-- Name: idx_market_buyer; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_buyer ON public.market USING btree (buyer);


--
-- Name: idx_market_listed_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_listed_at ON public.market USING btree (listed_at);


--
-- Name: idx_market_manipulation_species; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_manipulation_species ON public.market_manipulation_events USING btree (pokemon_species);


--
-- Name: idx_market_manipulation_type; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_manipulation_type ON public.market_manipulation_events USING btree (manipulation_type);


--
-- Name: idx_market_manipulation_users; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_manipulation_users ON public.market_manipulation_events USING gin (involved_user_ids);


--
-- Name: idx_market_owner; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_owner ON public.market USING btree (owner);


--
-- Name: idx_market_pokemon; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_pokemon ON public.market USING btree (poke);


--
-- Name: idx_market_price; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_market_price ON public.market USING btree (price);


--
-- Name: idx_milestone_progress_completed_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_milestone_progress_completed_at ON public.milestone_progress USING btree (completed_at);


--
-- Name: idx_milestone_progress_user_achievement; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_milestone_progress_user_achievement ON public.milestone_progress USING btree (u_id, achievement_type);


--
-- Name: idx_mv_trading_network_strength; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_mv_trading_network_strength ON public.mv_trading_network_graph USING btree (connection_strength);


--
-- Name: idx_mv_trading_network_user1; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_mv_trading_network_user1 ON public.mv_trading_network_graph USING btree (user1);


--
-- Name: idx_mv_trading_network_user2; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_mv_trading_network_user2 ON public.mv_trading_network_graph USING btree (user2);


--
-- Name: idx_pokemon_laundering_pokemon; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_pokemon_laundering_pokemon ON public.pokemon_laundering_history USING btree (pokemon_id);


--
-- Name: idx_pokemon_laundering_risk; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_pokemon_laundering_risk ON public.pokemon_laundering_history USING btree (risk_score) WHERE (flagged = true);


--
-- Name: idx_suspicious_trade_analytics_flags; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_suspicious_trade_analytics_flags ON public.suspicious_trade_analytics USING btree (flagged_alt_account, flagged_rmt, flagged_newbie_exploitation);


--
-- Name: idx_suspicious_trade_analytics_risk_score; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_suspicious_trade_analytics_risk_score ON public.suspicious_trade_analytics USING btree (overall_risk_score);


--
-- Name: idx_suspicious_trade_analytics_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_suspicious_trade_analytics_timestamp ON public.suspicious_trade_analytics USING btree (analysis_timestamp);


--
-- Name: idx_suspicious_trade_analytics_trade_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_suspicious_trade_analytics_trade_id ON public.suspicious_trade_analytics USING btree (trade_id);


--
-- Name: idx_trade_fraud_detections_analytics_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_analytics_id ON public.trade_fraud_detections USING btree (suspicious_trade_analytics_id);


--
-- Name: idx_trade_fraud_detections_fraud_type; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_fraud_type ON public.trade_fraud_detections USING btree (fraud_type);


--
-- Name: idx_trade_fraud_detections_primary_user; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_primary_user ON public.trade_fraud_detections USING btree (primary_user_id);


--
-- Name: idx_trade_fraud_detections_risk_score; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_risk_score ON public.trade_fraud_detections USING btree (risk_score);


--
-- Name: idx_trade_fraud_detections_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_status ON public.trade_fraud_detections USING btree (investigation_status);


--
-- Name: idx_trade_fraud_detections_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_timestamp ON public.trade_fraud_detections USING btree (detection_timestamp);


--
-- Name: idx_trade_fraud_detections_trade_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_trade_id ON public.trade_fraud_detections USING btree (trade_id);


--
-- Name: idx_trade_fraud_detections_trade_log_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_fraud_detections_trade_log_id ON public.trade_fraud_detections USING btree ("TradeLogTradeId");


--
-- Name: idx_trade_logs_receiver_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_logs_receiver_time ON public.trade_logs USING btree (receiver, "time");


--
-- Name: idx_trade_logs_sender_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_logs_sender_time ON public.trade_logs USING btree (sender, "time");


--
-- Name: idx_trade_logs_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_trade_logs_time ON public.trade_logs USING btree ("time");


--
-- Name: idx_user_loyalty_last_login; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_loyalty_last_login ON public.user_loyalty USING btree (last_login);


--
-- Name: idx_user_pokemon_ownership_breeding_female; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_pokemon_ownership_breeding_female ON public.user_pokemon_ownership USING btree (user_id, is_breeding_female) WHERE (is_breeding_female = true);


--
-- Name: idx_user_pokemon_ownership_pokemon; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_pokemon_ownership_pokemon ON public.user_pokemon_ownership USING btree (pokemon_id);


--
-- Name: idx_user_pokemon_ownership_pokemon_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_pokemon_ownership_pokemon_id ON public.user_pokemon_ownership USING btree (pokemon_id);


--
-- Name: idx_user_pokemon_ownership_user_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_pokemon_ownership_user_id ON public.user_pokemon_ownership USING btree (user_id);


--
-- Name: idx_user_pokemon_ownership_user_position; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX idx_user_pokemon_ownership_user_position ON public.user_pokemon_ownership USING btree (user_id, "position");


--
-- Name: idx_user_trade_relationships_flags; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_trade_relationships_flags ON public.user_trade_relationships USING btree (flagged_potential_alts, flagged_potential_rmt);


--
-- Name: idx_user_trade_relationships_risk; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_trade_relationships_risk ON public.user_trade_relationships USING btree (relationship_risk_score);


--
-- Name: idx_user_trade_relationships_updated; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_trade_relationships_updated ON public.user_trade_relationships USING btree (last_updated);


--
-- Name: idx_user_trade_relationships_users; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_user_trade_relationships_users ON public.user_trade_relationships USING btree (user1_id, user2_id);


--
-- Name: idx_users_market_banned; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_users_market_banned ON public.users USING btree (market_banned) WHERE (market_banned = true);


--
-- Name: idx_users_trade_banned; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_users_trade_banned ON public.users USING btree (trade_banned) WHERE (trade_banned = true);


--
-- Name: index_u_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX index_u_id ON public.egg_hatchery USING btree (u_id);


--
-- Name: new3_pokes_market_enlist_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX new3_pokes_market_enlist_idx ON public.pokes USING btree (market_enlist);


--
-- Name: pokes_market_enlist_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX pokes_market_enlist_idx ON public.dead_pokes USING btree (market_enlist);


--
-- Name: tokens_token_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX tokens_token_idx ON public.tokens USING btree (token);


--
-- Name: tokens_u_id_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX tokens_u_id_idx ON public.tokens USING btree (u_id);


--
-- Name: mothers expire_cooldown; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER expire_cooldown AFTER INSERT ON public.mothers FOR EACH STATEMENT EXECUTE FUNCTION public.expire_cooldown();


--
-- Name: market market_update_timestamp; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER market_update_timestamp BEFORE UPDATE ON public.market FOR EACH ROW EXECUTE FUNCTION public.update_market_updated_at();


--
-- Name: users trigger_insert_egg_hatchery; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trigger_insert_egg_hatchery AFTER INSERT ON public.users FOR EACH ROW EXECUTE FUNCTION public.insert_egg_hatchery();


--
-- Name: UserFilterCriteria FK_UserFilterCriteria_UserFilterGroups_FilterGroupId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserFilterCriteria"
    ADD CONSTRAINT "FK_UserFilterCriteria_UserFilterGroups_FilterGroupId" FOREIGN KEY ("FilterGroupId") REFERENCES public."UserFilterGroups"("Id") ON DELETE CASCADE;


--
-- Name: user_filter_criteria FK_user_filter_criteria_user_filter_groups_filter_group_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_filter_criteria
    ADD CONSTRAINT "FK_user_filter_criteria_user_filter_groups_filter_group_id" FOREIGN KEY (filter_group_id) REFERENCES public.user_filter_groups(id) ON DELETE CASCADE;


--
-- Name: cheststore cheststore_u_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.cheststore
    ADD CONSTRAINT cheststore_u_id_fkey FOREIGN KEY (u_id) REFERENCES public.users(u_id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_1; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_1 FOREIGN KEY ("1") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_10; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_10 FOREIGN KEY ("10") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_2; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_2 FOREIGN KEY ("2") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_3; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_3 FOREIGN KEY ("3") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_4; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_4 FOREIGN KEY ("4") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_5; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_5 FOREIGN KEY ("5") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_6; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_6 FOREIGN KEY ("6") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_7; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_7 FOREIGN KEY ("7") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_8; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_8 FOREIGN KEY ("8") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: egg_hatchery fk_egg_hatchery_9; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_egg_hatchery_9 FOREIGN KEY ("9") REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: user_pokemon_ownership fk_pokemon; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_pokemon_ownership
    ADD CONSTRAINT fk_pokemon FOREIGN KEY (pokemon_id) REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: pokemon_laundering_history fk_pokemon_laundering_pokemon; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pokemon_laundering_history
    ADD CONSTRAINT fk_pokemon_laundering_pokemon FOREIGN KEY (pokemon_id) REFERENCES public.pokes(id);


--
-- Name: suspicious_trade_analytics fk_suspicious_trade_analytics_trade_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.suspicious_trade_analytics
    ADD CONSTRAINT fk_suspicious_trade_analytics_trade_id FOREIGN KEY (trade_id) REFERENCES public.trade_logs(t_id) ON DELETE CASCADE;


--
-- Name: trade_fraud_detections fk_trade_fraud_detections_suspicious_analytics; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trade_fraud_detections
    ADD CONSTRAINT fk_trade_fraud_detections_suspicious_analytics FOREIGN KEY (suspicious_trade_analytics_id) REFERENCES public.suspicious_trade_analytics(id) ON DELETE SET NULL;


--
-- Name: trade_fraud_detections fk_trade_fraud_detections_trade_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trade_fraud_detections
    ADD CONSTRAINT fk_trade_fraud_detections_trade_id FOREIGN KEY (trade_id) REFERENCES public.trade_logs(t_id) ON DELETE SET NULL;


--
-- Name: trade_fraud_detections fk_trade_fraud_detections_trade_log; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trade_fraud_detections
    ADD CONSTRAINT fk_trade_fraud_detections_trade_log FOREIGN KEY ("TradeLogTradeId") REFERENCES public.trade_logs(t_id) ON DELETE SET NULL;


--
-- Name: user_pokemon_ownership fk_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_pokemon_ownership
    ADD CONSTRAINT fk_user FOREIGN KEY (user_id) REFERENCES public.users(u_id) ON DELETE CASCADE;


--
-- Name: user_risk_profiles fk_user_risk_profile_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_risk_profiles
    ADD CONSTRAINT fk_user_risk_profile_user FOREIGN KEY (user_id) REFERENCES public.users(u_id);


--
-- Name: egg_hatchery fk_users; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.egg_hatchery
    ADD CONSTRAINT fk_users FOREIGN KEY (u_id) REFERENCES public.users(u_id) ON DELETE CASCADE;


--
-- Name: market market_poke_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.market
    ADD CONSTRAINT market_poke_fkey FOREIGN KEY (poke) REFERENCES public.pokes(id) ON DELETE CASCADE;


--
-- Name: milestone_progress milestone_progress_u_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.milestone_progress
    ADD CONSTRAINT milestone_progress_u_id_fkey FOREIGN KEY (u_id) REFERENCES public.users(u_id) ON DELETE CASCADE;


--
-- Name: redeemstore redeemstore_u_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.redeemstore
    ADD CONSTRAINT redeemstore_u_id_fkey FOREIGN KEY (u_id) REFERENCES public.users(u_id) ON DELETE CASCADE;


--
-- Name: user_dittobitties user_dittobitties_dbitty_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_dittobitties
    ADD CONSTRAINT user_dittobitties_dbitty_id_fkey FOREIGN KEY (dbitty_id) REFERENCES public.dittobitties(id);


--
-- Name: user_loyalty user_loyalty_u_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.user_loyalty
    ADD CONSTRAINT user_loyalty_u_id_fkey FOREIGN KEY (u_id) REFERENCES public.users(u_id) ON DELETE CASCADE;


--
-- Name: users users_trade_ban_fraud_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_trade_ban_fraud_id_fkey FOREIGN KEY (trade_ban_fraud_id) REFERENCES public.trade_fraud_detections(id);



