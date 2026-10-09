# VFX Hider

A Dalamud plugin for FFXIV that cuts down visual noise from other players' VFX in busy venues, mainly modded dote effects.

## Usage

- `/vfxhider` opens the main window: the enable switch, the common-emote and minion options, and buttons for the other two windows.
- `/vfxhider on` toggles the vfx hiding funtionality to its on state. Simialr to the enable switch.
- `/vfxhider off` toggles the vfx hiding funtionality to its off state. Simialr to the enable switch.
- `/vfxhider users` lists players seen using modded VFX, with buttons to blacklist or whitelist them. (For if you find something more annoying but its not in those 5 defaults)
- `/vfxhider blacklist` shows the blacklist: who is on it, whether they are nearby, and how many of their effects were hidden. (thanks ptom for the idea)
- `/vfxhider log` opens the VFX spawn log. (Mainly for debugging. Not really needed for general use.)
- `/vfxhider config` opens the advanced settings: whitelist, extra emotes and the path-based rules.

Right-clicking a player adds or removes them from the whitelist or blacklist.

Rrquired plugins: [Penumbra](https://github.com/xivdev/Penumbra) otherwise you aren't using this to block modded plugins. 
