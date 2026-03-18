using System;

namespace NetDeamon.apps.MidiControl;

public class FaderWaveController
{
    private const int NumFaders = 8;
    private double _phase = 0.0;
    
    // Adjust these parameters to change the wave behavior
    private const double WaveSpeed = 0.02;      // How fast the wave moves (smaller = slower)
    private const double WaveFrequency = 0.5;   // Number of waves across the faders
    private const double WaveAmplitude = 40.0;  // Wave height (0-50 recommended)
    private const double BaseLevel = 50.0;      // Center position (0-100)
    
    public int[] GetFaderValues()
    {
        int[] values = new int[NumFaders];
        
        for (int i = 0; i < NumFaders; i++)
        {
            // Calculate sine wave value for this fader
            double angle = (i / (double)NumFaders) * Math.PI * 2.0 * WaveFrequency + _phase;
            double sineValue = Math.Sin(angle);
            
            // Convert to 0-100 range
            double faderValue = BaseLevel + (sineValue * WaveAmplitude);
            
            // Clamp to valid range and convert to int
            values[i] = (int)Math.Clamp(faderValue, 0.0, 100.0);
        }
        
        // Increment phase for next frame (creates the wandering effect)
        _phase += WaveSpeed;
        
        return values;
    }
}