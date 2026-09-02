-- MES -> AGV DO -> AUBO DI resident trigger template
--
-- Deploy this as a Script File in AuboStudio/AuboScope (or copy the contents
-- into a Script node on the teach pendant).  The file intentionally contains
-- no Modbus dependency and no network client: the AGV signal arrives on the
-- AUBO standard digital input connector.
--
-- Field values to confirm before enabling motion:
--   DI_PIN       = AUBO DI17 is pin 15 in the supplied default_0.ins report.
--   ACTIVE_HIGH  = true when a high level means ON; set false for active-low wiring.
--   ACK_DO_PIN   = optional AUBO output returned to an AGV DI for completion.
--   run_flow()   = paste one existing, already-verified motion sequence here.

local app = {}
local aubo = require('aubo')
local sched = sched or aubo.sched

local sleep = sched.sleep

app.PRIORITY = 1000
app.VERSION = "0.1"
app.VENDOR = "MES"

local DI_PIN = 15             -- DI17 (pin 15) in the supplied AUBO I/O report
local ACTIVE_HIGH = true      -- change only after measuring/confirming polarity
local ACK_DO_PIN = -1         -- e.g. 2 = AUBO DO02 / “小车DI7”; -1 disables ACK
local POLL_SECONDS = 0.05
local ACK_HOLD_SECONDS = 0.20

local function mes_di_active()
    local level = getStandardDigitalInput(DI_PIN)
    return ACTIVE_HIGH and level or not level
end

local function set_ack(value)
    if ACK_DO_PIN >= 0 then
        setStandardDigitalOutput(ACK_DO_PIN, value)
    end
end

local function run_flow(command_code)
    -- Replace this hook with an existing motion block only after the DI wiring
    -- is confirmed.  Example integration:
    --   if command_code == 1 then
    --       <paste the tested pick/dispense sequence here>
    --   end
    -- Keep all positions, gripper calls and speeds from the known-good project;
    -- do not invent new coordinates in this listener.
    textmsg("MES DI trigger received, command=" .. tostring(command_code))
end

function p_mes_di_trigger()
    local _ENV = sched.select_robot(1)

    -- Disable built-in input actions so this resident loop is the single owner
    -- of the trigger.  If you choose StartProgram(3) instead, do not deploy this
    -- loop at the same time on the same DI.
    setDigitalInputActionDefault()
    set_ack(false)

    local previous = mes_di_active()
    textmsg("MES DI listener online; DI pin=" .. tostring(DI_PIN))

    while true do
        local current = mes_di_active()
        -- Rising edge only: holding DO high cannot replay the same operation.
        if current and not previous then
            textmsg("MES DI rising edge")
            set_ack(true)
            local ok, detail = pcall(run_flow, 1)
            if not ok then
                textmsg("MES DI flow failed: " .. tostring(detail))
                set_ack(false)
            else
                sleep(ACK_HOLD_SECONDS)
                set_ack(false)
            end
        end
        previous = current
        sleep(POLL_SECONDS)
    end
end

function app:start(api)
    self.api = api
    p_mes_di_trigger()
end

function app:robot_error_handler(name, err)
    textmsg("MES DI listener robot error: " .. tostring(name) .. ": " .. tostring(err))
end

return app
