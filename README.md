# PVControl
An application to optimize (external) energy usage by monitoring and predicting battery SoC, houseload and PV charge.
It tries to always keep the battery SoC over the defined minima by checking if there will be enough PV charge or otherwise instruct to charge the battery at the cheapest times.
PVControl itself doesn't do anything but calculate the different entity states. You have to use the provided information and use automations to take advantage of this.

## Entities which should be used in automations
### sensor.pv_control_mode
This is the main information and has 3 possible values:
* "normal": normal operation, nothing special is happening
* "force_charge": instruct your inverter to charge the battery from grid. According to the prediction it's not possible to keep the SoC over the minima and now is the cheapest period to charge
* "grid_only": use as much grid power as possible (disable PV, charge the battery, use only grid power). This is only active if energy prices are negative, depending on your tarif you could even be paying for feedin
### sensor.pv_control_run_heavyloads_now
This sensor tells if it's a good time to use heavy loads now (washing machine, dishwasher, heatpump, etc) and also has 3 possible values:
* "Yes": use all the load you want, the battery will get full via PV charge
* "IfNecessary": only use needed loads, the battery won't reach 100% SoC but also will not go down to your preferred minima
* "No": try to avoid using loads, as it's predicted that the battery SoC will go under absolute minima

## Entities which could be used in automations
### binary_sensor.pv_control_need_to_charge_from_grid_today
This sensor just tells if it's necessary to charge the battery from grid today or if we can keep the SoC over the absolute minima until the next PV period

## Option entities
### select.pv_control_mode_override
Allows to manually override the current mode
### number.pv_control_preferredbatterycharge
The preferred minimal battery SoC, used as a lower limit for "pv_control_run_heavyloads_now"-"IfNecessary"
### switch.pv_control_enforce_preferred_soc
If active the application will keep the SoC always over the preferred SoC by charging from grid if necessary. Comparable to the "Backup Mode" of most inverters.
### switch.pv_control_force_charge_at_cheapest_period
If active the system will charge everday at the lowest price even if it wouldn't be necessary to keep the SoC over the minima
### number.pv_control_max_price_for_forcecharge
Only force charge at cheapest price if the price is below this value
### number.pv_control_forcecharge_target_soc
Don't charge over this SoC when force_charging in cheapest period_

## Info entities
Most of the other provided entities are just for information and are only relevant if you're a stickler for information overload like myself

## Entity attributes
all sensors have edditional information in the attributes, which again can be ignored

## Prediction
Class to encapsulate different predictions and forecasts.
For the load there only is a (weighted) average prediction available for now, but the logged data is meant to use some sort of ML/AI to predict the load more efficiently.

# DataLogger
This part logs the most important sensor information into a sqlite db to use for prediction.
