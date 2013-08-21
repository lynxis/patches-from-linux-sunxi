patches-from-linux-sunxi
========================
These are the patches based on jwrdego's linux-sunxi tree + 3 patches.
Look at my linux-sunxi fork.

After this huge diff I remove all sdks under /modules. 
Put subdirectories into seperate patch files and splitted them into smaller peaces.
That is also the reason for the naming and splitted /include patches.
A git tag is here created.
From here on I categorized the patches into 5 categories:
- sunxi: patches should used for sunxi
- refactor: these patches need to be splitted into smaller peaces
- think_about_it: these patches maybe required to get sunxi working
- maybe_late: patches which need later when sunxi is running a closer look
- ignore: these patches comes from android or aren't usefull for sunxi/suni7
