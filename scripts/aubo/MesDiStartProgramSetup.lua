-- One-time AUBO input-action setup for the simplest single-program flow.
-- Run this as a short Script File, then select/load the target project.  AUBO's
-- StandardInputAction.StartProgram value is 3 and fires on a DI rising edge.
-- Do not run this together with MesDiTrigger.lua on the same DI pin.

return function(api)
    local _ENV = require('aubo').sched.select_robot(1)
    local DI_PIN = 15 -- DI17 / pin 15 in the supplied default_0.ins report

    setStandardDigitalInputAction(DI_PIN, 3) -- StartProgram, rising edge
    textmsg("MES DI" .. tostring(DI_PIN) .. " configured as StartProgram(3)")
end
