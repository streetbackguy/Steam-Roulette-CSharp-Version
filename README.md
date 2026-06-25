# Steam Roulette (C Sharp Version)
Random game picker from your installed Steam games. Mainly to help indecisive people who want to play a game, but no idea which one to play. Now built in C# for more responsiveness.

# Usage
To use this application, you will need your Steam API key. This cannot be provided and is different for each person.

You can either edit the base script to add a constant for your API_KEY or you can enter it into the 'Enter Steam API Key' button dialogue box.

To get your Steam API Key, visit https://steamcommunity.com/dev/apikey and login. Remember to keep your API Key confidential and only for your eyes.

# Features
- This application will launch the chosen game for you directly from the Steam client.
- With each game chosen, it will display the Steam Header image for that game from the Steam servers. If no image is found on the server, it will look for a local Header file instead.
- This application has a button that will bring you to the games Store Page on https://steampowered.com/.
- If you're not particularly happy with one game, you can reroll with the reroll button.
- There is now an animation for when the wheel is spinning.
- Ability to set the number of games to spin through.
- Set your own API Key via the Set API Key button. This key will persist in a local text file so you only have to enter it once.
- Now an ability to switch between a light and dark theme, depending on your preferences.
- Preloads Steam game header images to a cache so you won't have to fetch them through the Steam API each time.
- Feature to now include games you own that are not installed in the spin list.
- Exclude games/items you don't want to be included in the spin.
- Exclude Games window now loads the games icons next to the name where possible and doesn't effect the functionality of the window.
- Log window to see for any errors downloading Game images/icons.

<img width="606" height="773" alt="image" src="https://github.com/user-attachments/assets/a2f59419-3d64-4bf3-884d-121b259f86f4" />

# To-Do
- General code optimisation
- Fix some icons not showing at all for some games in Exclude Games window
- Make the main window interactable whilst the Exclude Games window is active
- Fix window size so it becomes dynamic when a game with a longer title is spun
- Remember if Dark or Light mode has been toggled when the application was previously open

# Virus Scan

[https://www.virustotal.com/gui/file/f12f47273cb672a6f69022c9c5ed712420c36cc3c7c6ff1a73d15588ac385b9c/detection](https://www.virustotal.com/gui/file/ce094ba9ce7a9bedb8cb3e76bc3d9efb4ff27342aee17d8b02be01593e2a2bb2?nocache=1)

Added virus scan above for transparency. This application does scan your Steam libraries on your computer, so if it does get flagged as a virus, then it will be nothing more than a false positive.
