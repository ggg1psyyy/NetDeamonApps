# PVControl
An application to optimize (external) energy usage by monitoring and predicting battery SoC, houseload and PV charge.

## Prediction
Class to encapsulate different predictions and forecasts.
For the load there only is a (weighted) average prediction available for now, but the logged data is meant to use some sort of ML/AI to predict the load more efficient.

# DataLogger
This part logs the most important sensor information into a sqlite db to use for prediction.
