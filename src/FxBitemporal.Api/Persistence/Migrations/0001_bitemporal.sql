-- The bitemporal version table. One row = one fact, valid for a business-date range, believed for a known-time range.
-- Rows are never deleted or edited: a change of belief closes the known range of the old rows and inserts new rows.
CREATE TABLE fx.rate_version (
    id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    series       text        NOT NULL,
    valid_during daterange   NOT NULL,
    known_during tstzrange   NOT NULL,
    value        numeric     NOT NULL,
    unit         integer     NOT NULL,
    observed_on  date        NOT NULL,
    source       text        NOT NULL,
    reason       text,
    recorded_by  text        NOT NULL,

    CONSTRAINT series_is_tcmb_code CHECK (series ~ '^TP\.DK\.[A-Z]{3}\.[AS](\.EF)?\.YTL$'),
    CONSTRAINT value_positive CHECK (value > 0 AND value < 1e12),
    CONSTRAINT unit_positive CHECK (unit > 0),
    CONSTRAINT source_known CHECK (source IN ('tcmb-archive', 'evds', 'fixture', 'correction')),
    CONSTRAINT correction_has_reason CHECK (source <> 'correction' OR reason IS NOT NULL),
    CONSTRAINT reason_bounded CHECK (reason IS NULL OR length(reason) BETWEEN 1 AND 500),
    CONSTRAINT recorded_by_bounded CHECK (length(recorded_by) BETWEEN 1 AND 100),
    -- Half-open, finite lower bound, possibly infinite upper bound: [from, to) and [from, infinity).
    CONSTRAINT valid_during_well_formed CHECK (
        NOT isempty(valid_during) AND lower_inc(valid_during) AND NOT lower_inf(valid_during) AND NOT upper_inc(valid_during)),
    CONSTRAINT known_during_well_formed CHECK (
        NOT isempty(known_during) AND lower_inc(known_during) AND NOT lower_inf(known_during) AND NOT upper_inc(known_during)),
    -- A version carries a bulletin forward (weekends, holidays), never backwards.
    CONSTRAINT observed_not_after_valid CHECK (observed_on <= lower(valid_during)),
    -- The bitemporal invariant, enforced by the database: for one series, no two rows may overlap in valid time AND
    -- known time, so every (series, date, known-at) question has at most one answer. PostgreSQL 18's WITHOUT OVERLAPS
    -- keys cover a single period column; a rule over two periods needs an exclusion constraint on any version.
    CONSTRAINT no_bitemporal_overlap EXCLUDE USING gist (series WITH =, valid_during WITH &&, known_during WITH &&)
);

COMMENT ON TABLE fx.rate_version IS
    'TCMB indicative rates, bitemporal: valid_during = business dates the rate applies to, known_during = when the system believed it.';

-- The GiST index behind the exclusion constraint also answers as-of point queries
-- (series = $1 AND valid_during @> $2 AND known_during @> $3). Writers additionally need "the currently believed versions
-- of a series ordered by start", which this partial B-tree serves without touching closed history.
CREATE INDEX rate_version_current_by_start
    ON fx.rate_version (series, lower(valid_during))
    WHERE upper_inf(known_during);

-- Append-only enforcement, independent of which role connects (it also stops the owner and ad-hoc scripts).
CREATE FUNCTION fx.rate_version_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
        RAISE EXCEPTION 'fx.rate_version is append-only: versions are closed, never deleted'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF NOT upper_inf(OLD.known_during) THEN
        RAISE EXCEPTION 'version % is already closed and can no longer change', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.id, NEW.series, NEW.valid_during, NEW.value, NEW.unit, NEW.observed_on, NEW.source, NEW.reason, NEW.recorded_by)
           IS DISTINCT FROM
       (OLD.id, OLD.series, OLD.valid_during, OLD.value, OLD.unit, OLD.observed_on, OLD.source, OLD.reason, OLD.recorded_by)
       OR lower(NEW.known_during) <> lower(OLD.known_during)
       OR upper_inf(NEW.known_during) THEN
        RAISE EXCEPTION 'version %: the only permitted change is closing known_during', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER rate_version_append_only
    BEFORE UPDATE OR DELETE ON fx.rate_version
    FOR EACH ROW EXECUTE FUNCTION fx.rate_version_append_only();

CREATE TRIGGER rate_version_no_truncate
    BEFORE TRUNCATE ON fx.rate_version
    FOR EACH STATEMENT EXECUTE FUNCTION fx.rate_version_append_only();

-- Knowledge-time fence (docs/adr/0003): writers take per-series advisory locks exclusively, readers take them shared,
-- always in ascending key order so a multi-series writer and a multi-series reader cannot deadlock. A PL/pgSQL loop
-- makes the acquisition order explicit instead of relying on the evaluation order of a SELECT list.
CREATE FUNCTION fx.lock_series(p_series text[], p_shared boolean) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    k bigint;
BEGIN
    FOR k IN SELECT DISTINCT hashtextextended(s, 0) FROM unnest(p_series) AS s ORDER BY 1 LOOP
        IF p_shared THEN
            PERFORM pg_advisory_xact_lock_shared(k);
        ELSE
            PERFORM pg_advisory_xact_lock(k);
        END IF;
    END LOOP;
END
$$;

-- Least privilege for the service role: read, append, close. No DELETE, no UPDATE of any fact column.
REVOKE ALL ON fx.rate_version FROM PUBLIC;
GRANT SELECT, INSERT ON fx.rate_version TO fx_app;
GRANT UPDATE (known_during) ON fx.rate_version TO fx_app;
REVOKE ALL ON FUNCTION fx.lock_series(text[], boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION fx.lock_series(text[], boolean) TO fx_app;
