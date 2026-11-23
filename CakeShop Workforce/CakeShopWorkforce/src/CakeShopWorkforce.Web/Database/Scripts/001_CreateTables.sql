-- =====================================================================
-- 001_CreateTables.sql
-- Creates every table the app needs.
--
-- HOW THIS FILE RUNS:
--   DatabaseMigrator.cs runs each script in Database/Scripts ONE time,
--   in name order (001, 002, 003...). It remembers what already ran in
--   the "schema_versions" table. To change the database later, ADD a new
--   script (e.g. 004_AddColumnX.sql) - never edit a script that already ran.
--
-- NAMING: tables and columns use snake_case (employee_code).
--   Dapper maps them to C# PascalCase properties (EmployeeCode)
--   because Program.cs turns on MatchNamesWithUnderscores.
-- =====================================================================

-- ---------------------------------------------------------------------
-- STORE: the shop itself and its business settings
-- ---------------------------------------------------------------------
create table stores (
    id                         serial primary key,
    name                       text          not null,
    open_time                  time          not null default '07:00',
    close_time                 time          not null default '21:00',
    grace_minutes              int           not null default 5,     -- minutes allowed before someone is "late"
    regular_hours_per_day      numeric(4,2)  not null default 8,     -- worked hours above this = overtime
    target_tx_per_personnel    int           not null default 40,    -- benchmark used by the simple ideal-manpower formula
    target_tx_per_person_hour  int           not null default 12,    -- benchmark used for hourly / per-shift analysis
    weekly_ot_alert_hours      numeric(5,2)  not null default 6,     -- alert when one person passes this many OT hours in 7 days
    created_at                 timestamptz   not null default now()
);

-- ---------------------------------------------------------------------
-- STATIONS: work areas. Production, Decorating, Cashier, Sales.
--   driver = 'Transactions' -> needed people grow with customer transactions
--   driver = 'Fixed'        -> needed people = min_headcount (e.g. bakers)
-- ---------------------------------------------------------------------
create table stations (
    id                   serial primary key,
    store_id             int     not null references stores(id),
    name                 text    not null,
    color                text    not null default '#5B5FC7',
    driver               text    not null default 'Fixed' check (driver in ('Transactions', 'Fixed')),
    target_tx_per_person int     null,              -- only used when driver = 'Transactions'
    min_headcount        int     not null default 1,
    sort_order           int     not null default 0
);

-- ---------------------------------------------------------------------
-- SHIFT TEMPLATES: Opening, Mid, Closing, Full
-- ---------------------------------------------------------------------
create table shift_templates (
    id             serial primary key,
    store_id       int     not null references stores(id),
    code           text    not null,               -- short label shown on the calendar (OPEN, MID...)
    name           text    not null,
    start_time     time    not null,
    end_time       time    not null,
    break_minutes  int     not null default 60,    -- unpaid meal break
    color          text    not null default '#5B5FC7',
    sort_order     int     not null default 0,
    is_active      boolean not null default true
);

-- ---------------------------------------------------------------------
-- EMPLOYEES: the masterlist
-- ---------------------------------------------------------------------
create table employees (
    id                  serial primary key,
    store_id            int     not null references stores(id),
    employee_code       text    not null,
    full_name           text    not null,
    position            text    not null,
    department          text    not null default 'Store Operations',
    station_id          int     null references stations(id),
    employment_status   text    not null default 'Regular',     -- Regular, Probationary, Contractual, Trainee
    employment_type     text    not null default 'Full-time',   -- Full-time, Part-time
    contact_number      text    null,
    email               text    null,
    available_days      text    not null default 'Mon,Tue,Wed,Thu,Fri,Sat,Sun',
    max_hours_per_week  int     not null default 48,
    rest_day            text    null,                           -- usual rest day: Mon..Sun
    is_active           boolean not null default true,
    is_demo             boolean not null default false,          -- true = created by "Load demo data"
    created_at          timestamptz not null default now(),
    unique (store_id, employee_code)
);

-- ---------------------------------------------------------------------
-- SCHEDULE: one row per employee per day (so duplicates are impossible)
--   assignment_type: 'Shift', 'RestDay' or 'Leave'
-- ---------------------------------------------------------------------
create table schedule_assignments (
    id                 serial primary key,
    store_id           int     not null references stores(id),
    employee_id        int     not null references employees(id),
    business_date      date    not null,
    assignment_type    text    not null check (assignment_type in ('Shift', 'RestDay', 'Leave')),
    shift_template_id  int     null references shift_templates(id),
    station_id         int     null references stations(id),
    start_time         time    null,
    end_time           time    null,
    break_minutes      int     not null default 0,
    leave_type         text    null,           -- Vacation, Sick, Emergency, Service Incentive...
    note               text    null,
    is_demo            boolean not null default false,
    updated_at         timestamptz not null default now(),
    updated_by         text    null,
    unique (employee_id, business_date)
);
create index ix_schedule_store_date on schedule_assignments (store_id, business_date);

-- Publish status for each week (Monday = week_start)
create table schedule_weeks (
    store_id               int     not null references stores(id),
    week_start             date    not null,
    status                 text    not null default 'Draft' check (status in ('Draft', 'Published')),
    published_at           timestamptz null,
    published_by           text    null,
    changed_after_publish  boolean not null default false,
    primary key (store_id, week_start)
);

-- ---------------------------------------------------------------------
-- ATTENDANCE: actual time in / time out
-- ---------------------------------------------------------------------
create table attendance (
    id             serial primary key,
    store_id       int     not null references stores(id),
    employee_id    int     not null references employees(id),
    business_date  date    not null,
    time_in        time    null,
    time_out       time    null,
    break_minutes  int     not null default 60,
    is_absent      boolean not null default false,
    note           text    null,
    is_demo        boolean not null default false,
    updated_at     timestamptz not null default now(),
    updated_by     text    null,
    unique (employee_id, business_date)
);
create index ix_attendance_store_date on attendance (store_id, business_date);

-- ---------------------------------------------------------------------
-- SALES: daily totals + optional hourly breakdown (from the POS)
-- ---------------------------------------------------------------------
create table sales_daily (
    store_id       int            not null references stores(id),
    business_date  date           not null,
    transactions   int            not null default 0,
    sales          numeric(12,2)  not null default 0,
    is_demo        boolean        not null default false,
    updated_at     timestamptz    not null default now(),
    primary key (store_id, business_date)
);

create table sales_hourly (
    store_id       int            not null references stores(id),
    business_date  date           not null,
    hour           int            not null check (hour between 0 and 23),
    transactions   int            not null default 0,
    sales          numeric(12,2)  not null default 0,
    is_demo        boolean        not null default false,
    primary key (store_id, business_date, hour)
);

-- ---------------------------------------------------------------------
-- USERS: people who can sign in. role = Owner, Manager, Supervisor, Staff
-- ---------------------------------------------------------------------
create table users (
    id             serial primary key,
    user_name      text    not null unique,
    display_name   text    not null,
    password_hash  text    not null,
    role           text    not null check (role in ('Owner', 'Manager', 'Supervisor', 'Staff')),
    employee_id    int     null references employees(id),   -- links a Staff login to their employee record
    is_active      boolean not null default true,
    created_at     timestamptz not null default now()
);

-- ---------------------------------------------------------------------
-- AUDIT LOG: who changed what, and when
-- ---------------------------------------------------------------------
create table audit_log (
    id          bigserial primary key,
    store_id    int          not null,
    at          timestamptz  not null default now(),
    user_name   text         not null,
    action      text         not null,     -- e.g. "Assign shift", "Publish schedule"
    entity      text         not null,     -- e.g. "Schedule", "Employee"
    entity_id   text         null,
    details     text         null
);
create index ix_audit_store_at on audit_log (store_id, at desc);
