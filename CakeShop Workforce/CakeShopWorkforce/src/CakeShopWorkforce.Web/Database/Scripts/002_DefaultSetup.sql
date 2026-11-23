-- =====================================================================
-- 002_DefaultSetup.sql
-- Starting configuration: one store, four stations, four shift templates.
-- These are SETTINGS, not operational data - change them in the app
-- under Settings. No employees, sales or attendance are created here.
-- =====================================================================

insert into stores (id, name, open_time, close_time)
values (1, 'My Cake Shop', '07:00', '21:00');

-- keep the serial counter in sync because we inserted id = 1 manually
select setval(pg_get_serial_sequence('stores', 'id'), 1);

insert into stations (store_id, name, color, driver, target_tx_per_person, min_headcount, sort_order) values
    (1, 'Production', '#C4314B', 'Fixed',        null, 2, 1),
    (1, 'Decorating', '#8764B8', 'Fixed',        null, 1, 2),
    (1, 'Cashier',    '#0F6CBD', 'Transactions', 120,  1, 3),
    (1, 'Sales',      '#13A10E', 'Transactions', 90,   1, 4);

insert into shift_templates (store_id, code, name, start_time, end_time, break_minutes, color, sort_order) values
    (1, 'OPEN',  'Opening Shift', '06:00', '15:00', 60, '#0F6CBD', 1),
    (1, 'MID',   'Mid Shift',     '10:00', '19:00', 60, '#8764B8', 2),
    (1, 'CLOSE', 'Closing Shift', '13:00', '22:00', 60, '#C4314B', 3),
    (1, 'FULL',  'Full Shift',    '08:00', '20:00', 60, '#CA5010', 4);
