build instructions
------------------

former instruction
++++++++++++++++++

call "$(ProjectDir)buildclrmodule.bat" $(Platform) "$(ProjectDir)" "$(TargetDir)clr.pyd"
if NOT EXIST "$(SolutionDir)\py$(OutDir)" mkdir "$(SolutionDir)\py$(OutDir)"
copy "$(TargetPath)" "$(SolutionDir)\py$(OutDir)"
copy "$(TargetDir)*.pdb" "$(SolutionDir)\py$(OutDir)"
copy "$(TargetDir)clr.pyd" "$(SolutionDir)\py$(OutDir)"
copy "$(TargetDir)*.pyd" "$(SolutionDir)\build\lib.win-amd64-3.5"
copy "$(TargetDir)*.dll" "$(SolutionDir)\build\lib.win-amd64-3.5"