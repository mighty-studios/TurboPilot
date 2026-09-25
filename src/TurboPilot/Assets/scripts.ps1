# TurboPilot helper functions.
#
# Tools -> Open Powershell dots this file into a new shell opened in the
# session workspace. The copy under %LOCALAPPDATA%\TurboPilot\scripts is
# yours to edit: it is created from this default only when missing, and is
# never overwritten. Delete it to go back to these defaults.
write-output "[current_root] return the root folder name of the current git repo"
function current_root
{ 
	return . git rev-parse --show-toplevel
}

write-output "[home] go to 'home' folder of current repo"
function home
{ 
	$rootname = current_root
	set-location $rootname
}

write-output "[exp] open explorer in the current folder"
function exp
{ 
	& explorer .
}

write-output "[clx] clear screen, but retain history"
function clx($SaveRows) {
    If ($SaveRows) {
        [System.Console]::SetWindowPosition(0,[System.Console]::CursorTop-($SaveRows+1))
    } Else {
        [System.Console]::SetWindowPosition(0,[System.Console]::CursorTop)
   }
}

write-output "[current_branch] get the name of the current branch"
function current_branch() {
    return . git rev-parse --abbrev-ref HEAD
}

function continueOrAbort([string]$question){
	Write-Host "$question `n[c]ontinue, [a]bort?"
	$x = " "
	while (($x -ne 'c') -and ($x -ne 'a'))
	{
		$input = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
		$x = $input.Character
	}
	
	Write-Host "got it!"	
	return ($x -eq 'c')
}

function yesOrNo([string]$question){
	Write-Host "$question `n[y]es, [n]0?"
	$x = " "
	while (($x -ne 'y') -and ($x -ne 'n'))
	{
		$input = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
		$x = $input.Character
	}
	
	Write-Host "got it!"	
	return ($x -eq 'y')
}

function do_hard_reset() {
    $current = current_branch
	write-output "command: git reset --hard origin/$current"
	. git reset --hard origin/$current
	
	cleanup
	prompt_for_update
}

write-output "[hard_reset] do a git reset --hard on the current branch"
function hard_reset() {
	$check= continueOrAbort "full reset to head of branch? All local edits will be lost." 
	if ($check -eq $true)
	{
		do_hard_reset
	}
}

function deleteTempFile([string]$tempfile)
{
	$exists = Test-Path $tempfile
	if ($exists)
	{
		Get-ChildItem $tempfile |? { -not $_.IsReadOnly } |% { Write-Host "[deleted temp file] " + $_.FullName; Remove-Item $_ };
	}
}

function handle_merge([string]$tempfile) {
	$exists = Test-Path $tempfile
	$splitOption = [System.StringSplitOptions]::RemoveEmptyEntries
	
	. git config --global mergetool.bcright.path "c:/Program Files/Beyond Compare 4/bcomp.exe $LOCAL $REMOTE $BASE $MERGED /automerge /favorright" 
	
	while ($exists -eq $true)
	{	
		write-output "looking for merge issues..."
		write-output $rebaseResults
		
		$mergeErrors = $false
		
		$reader = [System.IO.File]::OpenText($tempfile)
		while($null -ne ($line = $reader.ReadLine())) 
		{
			$phrase= "Merge conflict in "
			$phraseLength = $phrase.Length
			
			$phrasePos = $line.IndexOf($phrase)
			if ($phrasePos -gt 0)
			{
				$filename = $line.Substring($phrasePos + $phraseLength)
				write-output "merge issue in: $filename"
				$mergeErrors = $true
				$x = " "
				do
				{
					write-output "[m]erge, [s]kip or [a]bort?"
					$input = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
					$x = $input.Character
					write-output $x
				} while (($x -ne 'm') -and ($x -ne 's') -and ($x -ne 'a'))
				
				if ($x -eq 'm')
				{
					write-output "calling . git mergetool $filename -tbcright"
					. git mergetool $filename -tbcright
					#. git mergetool $filename
				}
				elseif ($x -eq 'a')
				{
					$reader.Close
					deleteTempFile $tempfile
					. git rebase --abort
					exit
				}
			}
			else {
				write-output $line
			}
		}
		
		$reader.Close()
		$reader.Dispose()
		
		deleteTempFile $tempfile
		$exists = $false
		
		if ($mergeErrors -eq $true)
		{
			do
			{
				write-output "[c]ontinue, [e]xit or [a]bort?"
				$input = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
				$x = $input.Character
				write-output $x
			} while (($x -ne 'c') -and ($x -ne 'e') -and ($x -ne 'a'))
			
			if ($x -eq 'c')
			{
				. git rebase --continue | Out-File $tempfile
				$exists = Test-Path $tempfile
			}
			elseif ($x -eq 'a')
			{
				. git rebase --abort
			}
		}
	}
}

write-output "[rebase_to <target>] interactive rebase to target branch"
function rebase_to([string]$target) {
	if ($target.length -gt 0) {
				
		if (continueOrAbort("Rebase local branch to origin/"+$target+"?") -eq $true)
		{
			$tempfile = "$env:SCRIPTS_FOLDER\__temp_rebase_output.txt"
			deleteTempFile $tempfile

			write-output "command: rebase -i -Xignore-space-change origin/$target"
			write-output "waiting for interactive edit..."
			. git rebase -i -Xignore-space-change "origin/$target" | Out-File $tempfile
			
			handle_merge $tempfile
			cleanup
			prompt_for_update
		}
	}
}

write-output "[rebase_back <count>] interactive rebase to a commit <count> steps in the past"
function rebase_back([int]$count) {
	if ($count -gt 0) {
				
		if (continueOrAbort("Rebase current branch to HEAD~"+$count+"?") -eq $true)
		{
			$tempfile = "$env:SCRIPTS_FOLDER\__temp_rebase_output.txt"
			deleteTempFile $tempfile

			write-output "command: rebase -i -Xignore-space-change HEAD~$count"
			write-output "waiting for interactive edit..."
			. git rebase -i -Xignore-space-change "HEAD~$count" | Out-File $tempfile
			
			handle_merge $tempfile
		}
	}
}
write-output "[rebase_on_commit <target>] interactive rebase to target commit"
function rebase_range([string]$commit, [string]$start, [string]$end) {
	if ($commit.length -gt 0) {
				
		$tempfile = "$env:SCRIPTS_FOLDER\__temp_rebase_output.txt"
		deleteTempFile $tempfile

		write-output "command: rebase -i -Xignore-space-change --onto $commit $start $end"
		write-output "waiting for interactive edit..."
		. git rebase -i -Xignore-space-change --onto $commit $start $end | Out-File $tempfile
		
		handle_merge $tempfile
		cleanup
		prompt_for_update
	}
}

write-output "[find_branch <target>] determine if a branch exists"
function find_branch([string]$target) {
	if ($target.length -gt 0) 
	{
		$pattern = "*/" + $target
		$result= git branch -a --list $pattern
		if ($result.length -gt 0) 
		{
			return $true
		}
	}
	return $false
}

write-output "[fetch_branch <target>] fetch a branch"
function fetch_branch([string]$target) {
	$found = find_branch $target
	if ($found -eq $false)
	{
		if (continueOrAbort("branch " + $target+ " was not found. Attempt to fetch and map remote?") -eq $true)
		{
			. git fetch origin $target
			. git branch $target FETCH_HEAD -u origin/$target
			$found = find_branch $target
		}
		
		if ($found -eq $false)
		{
			write-output "branch $target was not found. Do you have the name right?"
			return $false
		}
	}
	else {
		. git fetch origin $target
	}
}

function switch_to_internal([string]$target, [bool]$force) {
	
	$local_branch = current_branch
	$compare = [string]::Compare($target, $local_branch, $True)
	if ($compare -eq 0)
	{
		write-output "branch $target is already the current branch"
		return $false
	}
	
	$found = find_branch $target
	if ($found -eq $false)
	{
		if (yesOrNo("branch $target was not found. Attempt to fetch and map remote?") -eq $true)
		{
			. git fetch origin $target
			. git checkout -b $target origin/$target
			$found = find_branch $target
		}
		
		if ($found -eq $false)
		{
			write-output "branch $target was not found. Do you need to 'git fetch' first?"
			return $false
		}
	}
		
	write-output "showing git status..."
	. git status
	if (continueOrAbort("Switch to branch "+$target+"? Local edits may be lost if you did not stash or commit them.") -eq $true)
	{
		. git rebase --abort
		. git checkout -- .
		$SubmoduleUpdate= $false
		if ($force -ne $true)
		{
			if (yesOrNo("Update submodules and nuget, too?") -eq $true)
			{
				. git submodule deinit --all --force
				$SubmoduleUpdate= $true
			}
		}
		else
		{
			. git submodule deinit --all --force
			$SubmoduleUpdate= $true
		}

		#check out the new branch
		write-output "command: git checkout $target"
		. git checkout $target

		#make sure we succeeded
		$new_branch = current_branch
		$compare = [string]::Compare($target, $new_branch, $True)
		if ($compare -eq 0)
		{
			. git reset --hard origin/$current
			cleanup
			if ($force -eq $true)
			{
				. git pull --rebase --ff-only origin $current
			}
			if ($SubmoduleUpdate -eq $true)
			{
				submodule_update
			}
			return $true
		}
		
		write-output "Switch_to $target failed! Current branch is $new_branch"
	}
	
	return ($false)
}

write-output "[switch_to <target>] switch to a target branch"
function switch_to([string]$target) {
	switch_to_internal $target $false
}

write-output "[full_switch_to <target>] switch to a target branch without prompting about submodules"
function full_switch_to([string]$target) {
	switch_to_internal $target $true
}

write-output "[append_from <source>] append changes from a source branch"
function append_from([string]$source) {
	if ($source.length -gt 0) {
		if (continueOrAbort("Append commits from "+$source+" to local branch?") -eq $true)
		{
			write-output "command:  git merge $source --ff-only"
			. git merge $source --ff-only
			
			cleanup
			prompt_for_update
		}
	}
}

write-output "[get_latest] append changes from a source branch"
function get_latest() {
	$current = current_branch
	$text= "Pull in latest " + $current + " commits from Git?"
	if (continueOrAbort($text) -eq $true)
	{
		write-output "git pull --rebase origin $current"
		. git pull --rebase origin $current
	}
}

write-output "[new_branch] create a new branch with the given name"
function new_branch([string]$name) 
{
	$result= $false
	if ($name -is [String]) 
	{
		$name = $name.Trim()
		
		$create= $false
		$tempfile = "$env:SCRIPTS_FOLDER\__temp_branch_names.txt"
		deleteTempFile $tempfile
		
		. git branch --list $name | Out-File $tempfile
		
		$exists = Test-Path $tempfile
		if ($exists -eq $true)
		{	
			$create = $true
			$reader = [System.IO.File]::OpenText($tempfile)
			while($null -ne ($line = $reader.ReadLine())) 
			{
				$branch = $line.Trim()
				write-output "testing: $line [$branch]"
				$compare = [string]::Compare($name, $branch, $True)
				write-output "compare result= $compare"
				if ($compare -eq 0)
				{
					write-output "$name already exists in branch names: $line"
					$create= $false
				}
			}
			$reader.Close()
			$reader.Dispose()			
		}
		else
		{
			write-output "temp file not found. script failed." 
		}
		
		if ($create -eq $true)
		{
			write-output "ok, making branch $name ..."
			. git checkout -b $name
			$new_branch = current_branch
			$compare = [string]::Compare($name, $new_branch, $True)
			if ($compare -eq 0)
			{
				write-output "branch created. Establishing tracking"
				. git push -u origin $name
				$result= $true
			}
		}
	}
	else
	{
		write-output "invalid name"
	}
	
	deleteTempFile $tempfile
	
	return $result
}

write-output "[submodule_update] update git submodules"
function submodule_update(){
	write-output "Disconnecting submodules. Please wait..."
	
	$rootname = current_root	
	pushd $rootname
	. git submodule deinit --all --force
	$exists = Test-Path .\.git\modules
	if ($exists)
	{
		Remove-Item .\.git\modules -Force -Recurse
	}
	
	write-output "reconnecting submodules. Please wait..."
	. git clean -ffxd | out-null
	
	write-output "reconnecting submodules. Please wait..."
	. git submodule update --init --recursive
	. git submodule update --remote
	popd
}

write-output "[cleanup] git clean"
function cleanup(){
		write-output "command: git clean -ffxd (Please wait...)"
		git clean -ffdx | out-null
}
	
function prompt_for_update(){
    if (yesOrNo("Update submodules and nuget?") -eq $true)
    {
	    submodule_update
    }
}

write-output "[refetch] git fetch and hard reset the local branch"
function refetch(){
	$current = current_branch
	if (yesOrNo("Fetch and hard-reset $current from Git?") -eq $true)
	{
		fetch_branch $current
		do_hard_reset
	}
}

write-output "[gitdiff] diff commitA vs commit B"
function gitdiff ([string]$A, [string]$B){
	write-output "command example:  git difftool --dir-diff head head~2"
	write-output "command request:  git difftool --dir-diff $A $B"
	. git difftool --dir-diff $A $B
}

function TimedPrompt($prompt, [int]$secondsToWait){   
    Write-Host -NoNewline "$prompt (q to quit)"
    [int]$secondsCounter = 0
    [int]$subCounter = 0
	$sleepTimer=500
	$QuitKey=81
	$keyHit= 0
    While ( ($keyHit -ne $QuitKey) -and ($secondsCounter -lt $secondsToWait) )
	{
		if($host.UI.RawUI.KeyAvailable) {
			$key = $host.ui.RawUI.ReadKey("NoEcho,IncludeKeyUp")
			if($key.VirtualKeyCode -eq $QuitKey) {
				#For Key Combination: eg., press 'LeftCtrl + q' to quit.
				#Use condition: (($key.VirtualKeyCode -eq $Qkey) -and ($key.ControlKeyState -match "LeftCtrlPressed"))
				Write-Host -ForegroundColor Yellow ("'q' is pressed! Stopping the script now.")
				return $true
			}
		}	
		
        Start-Sleep -m $sleepTimer
        $subCounter = $subCounter + $sleepTimer
        if($subCounter -ge 1000)
        {
            $secondsCounter++
            $subCounter = 0
            Write-Host -NoNewline "."
        }       
        if ($secondsCounter -eq $secondsToWait) { 
            Write-Host "finished `r`n"
            return $false
        }
    }
    
	Write-Host "`r`n"
    return $false
}

write-output "[clone_to_subfolder] clone with submodules into a named subfolder"
function clone_to_subfolder ([string]$GITPATH,[string]$FOLDER){
	if ($FOLDER -is [String]) 
	{
		$FOLDER = $FOLDER.Trim()
		$text= "clone " + $GITPATH + " to " + $folder
		if (continueOrAbort($text) -eq $true)
		{
			$exists = Test-Path $FOLDER
			if ($exists -ne $true)
			{
				git clone --recurse-submodules -j8 $GITPATH $FOLDER
				$exists = Test-Path $FOLDER
				if ($exists -eq $true)
				{
					cd $FOLDER
					write-output "fetching submodules. Please wait..."
					. git submodule update --init --recursive
					write-output "Ready!"
				}
				else 
				{
					write-output "git failed to create local directory"
				}
			}
			else 
			{
				write-output "non-empty local directory already exists. Aborting"
			}
		}
	}
	else
	{
		write-output "need to specify a subfolder name"
	}
}

write-output "[vs22] launch visual studio 2022"
function vs22()
{
	$devenv = $null

	# Preferred: ask vswhere (ships with any VS 2017+ installer at a fixed location)
	$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
	if (Test-Path $vswhere)
	{
		$devenv = & $vswhere -latest -prerelease -property productPath 2>$null
		if ($devenv -and -not (Test-Path $devenv))
		{
			$devenv = $null
		}
	}

	# Fallback: probe common install locations across editions
	if (-not $devenv)
	{
		$roots = @(
			"${env:ProgramFiles}\Microsoft Visual Studio",
			"${env:ProgramFiles(x86)}\Microsoft Visual Studio"
		) | Where-Object { $_ -and (Test-Path $_) }

		$editions = @('Enterprise','Professional','Community','BuildTools','Preview')

		:outer foreach ($root in $roots)
		{
			$years = Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
				Sort-Object Name -Descending
			foreach ($year in $years)
			{
				foreach ($edition in $editions)
				{
					$candidate = Join-Path $year.FullName "$edition\Common7\IDE\devenv.exe"
					if (Test-Path $candidate)
					{
						$devenv = $candidate
						break outer
					}
				}
			}
		}
	}

	if ($devenv)
	{
		write-output "launching: $devenv"
		. $devenv
	}
	else
	{
		write-output "Visual Studio devenv.exe was not found. Is Visual Studio installed?"
	}
}

write-output "[vscode] launch Visual Studio Code (detached)"
function vscode()
{
	Start-Process -FilePath "cmd.exe" -ArgumentList '/c','start','""','"C:\Program Files\Microsoft VS Code\Code.exe"','.' -WindowStyle Hidden
}
