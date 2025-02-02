﻿using FluentValidation;
using LXP.Common.Constants;
using LXP.Common.Entities;
using LXP.Common.Validators;
using LXP.Common.ViewModels.QuizQuestionViewModel;
using LXP.Core.IServices;
using LXP.Data.IRepository;
using Microsoft.AspNetCore.Http;
using OfficeOpenXml;
using Serilog;

namespace LXP.Core.Services
{
    public class BulkQuestionService : IBulkQuestionService
    {
        private readonly IBulkQuestionRepository _bulkQuestionRepository;
        private readonly IQuizQuestionRepository _quizQuestionRepository;
        private readonly BulkQuizQuestionViewModelValidator _validator;

        public BulkQuestionService(
            IBulkQuestionRepository bulkQuestionRepository,
            IQuizQuestionRepository quizQuestionRepository,
            BulkQuizQuestionViewModelValidator validator
        )
        {
            _bulkQuestionRepository = bulkQuestionRepository;
            _quizQuestionRepository = quizQuestionRepository;
            _validator = validator;
        }

        public async Task<object> ImportQuizDataAsync(IFormFile file, Guid quizId)
        {
            if (file == null || file.Length <= 0)
                throw new ArgumentException("File is empty.");

            var quizQuestions = new List<BulkQuizQuestionViewModel>();

            try
            {
                using (var stream = new MemoryStream())
                {
                    await file.CopyToAsync(stream);
                    stream.Position = 0;

                    using (ExcelPackage package = new ExcelPackage(stream))
                    {
                        ExcelWorksheet worksheet = package.Workbook.Worksheets.FirstOrDefault();
                        if (worksheet == null)
                            throw new ArgumentException("Worksheet not found.");

                        for (int row = 3; row <= worksheet.Dimension.End.Row; row++)
                        {
                            string type = worksheet.Cells[row, 2].Value?.ToString();

                            if (
                                type == QuizQuestionTypes.MultiChoiceQuestion
                                || type == QuizQuestionTypes.TrueFalseQuestion
                                || type == QuizQuestionTypes.MultiSelectQuestion
                            )
                            {
                                var quizQuestion = new BulkQuizQuestionViewModel
                                {
                                    QuestionType = type,
                                    Question = worksheet.Cells[row, 3].Value?.ToString(),
                                };

                                if (
                                    string.IsNullOrEmpty(quizQuestion.QuestionType)
                                    || string.IsNullOrEmpty(quizQuestion.Question)
                                )
                                    continue;

                                if (type == QuizQuestionTypes.MultiChoiceQuestion)
                                {
                                    quizQuestion.Options = ExtractOptions(
                                        worksheet,
                                        row,
                                        4,
                                        4,
                                        type
                                    );
                                    quizQuestion.CorrectOptions = ExtractOptions(
                                        worksheet,
                                        row,
                                        12,
                                        1,
                                        type
                                    );
                                    if (!ValidateMCQOptions(quizQuestion))
                                        continue;
                                }
                                else if (type == QuizQuestionTypes.TrueFalseQuestion)
                                {
                                    quizQuestion.Options = ExtractOptions(
                                        worksheet,
                                        row,
                                        4,
                                        2,
                                        type
                                    );
                                    quizQuestion.CorrectOptions = ExtractOptions(
                                        worksheet,
                                        row,
                                        12,
                                        1,
                                        type
                                    );
                                    if (!ValidateTFOptions(quizQuestion))
                                        continue;
                                }
                                else if (type == QuizQuestionTypes.MultiSelectQuestion)
                                {
                                    int optionCount = GetMSQOptionCount(worksheet, row);
                                    quizQuestion.Options = ExtractOptions(
                                        worksheet,
                                        row,
                                        4,
                                        optionCount,
                                        type
                                    );
                                    quizQuestion.CorrectOptions = ExtractOptions(
                                        worksheet,
                                        row,
                                        12,
                                        GetMSQCorrectOptionCount(optionCount),
                                        type
                                    );
                                    if (!ValidateMSQOptions(quizQuestion))
                                        continue;
                                }

                                quizQuestions.Add(quizQuestion);
                            }
                        }
                    }
                }

                var validQuizQuestions = new List<BulkQuizQuestionViewModel>();
                foreach (var quizQuestion in quizQuestions)
                {
                    var validationResult = _validator.Validate(quizQuestion);
                    if (!validationResult.IsValid)
                    {
                        Log.Warning(
                            $"Question validation failed: {string.Join(" ", validationResult.Errors.Select(e => e.ErrorMessage))}"
                        );
                        continue; // Skip invalid questions
                    }

                    validQuizQuestions.Add(quizQuestion);
                }

                foreach (var quizQuestion in validQuizQuestions)
                {
                    int nextQuestionNo = await _quizQuestionRepository.GetNextQuestionNoAsync(
                        quizId
                    );

                    QuizQuestion questionEntity = new QuizQuestion
                    {
                        QuizId = quizId,
                        QuestionNo = nextQuestionNo,
                        QuestionType = quizQuestion.QuestionType,
                        Question = quizQuestion.Question,
                        CreatedBy = "Admin",
                        CreatedAt = DateTime.Now
                    };

                    await _bulkQuestionRepository.AddQuestionsAsync(
                        new List<QuizQuestion> { questionEntity }
                    );

                    var optionEntities = quizQuestion
                        .Options.Where(option => !string.IsNullOrEmpty(option))
                        .Select(option => new QuestionOption
                        {
                            QuizQuestionId = questionEntity.QuizQuestionId,
                            Option = option,
                            IsCorrect = quizQuestion.CorrectOptions.Contains(option),
                            CreatedAt = DateTime.Now,
                            CreatedBy = "Admin",
                            ModifiedBy = "Admin"
                        })
                        .ToList();

                    await _bulkQuestionRepository.AddOptionsAsync(
                        optionEntities,
                        questionEntity.QuizQuestionId
                    );
                }

                return validQuizQuestions;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred while importing quiz data.");
                if (ex.InnerException != null)
                {
                    Log.Error(ex.InnerException, "Inner exception details.");
                }
                return new
                {
                    statusCode = 500,
                    message = $"An error occurred while importing quiz data: {ex.Message}",
                    data = (object)null
                };
            }
        }

        private bool ValidateTFOptions(BulkQuizQuestionViewModel quizQuestion)
        {
            return quizQuestion.Options.Length == 2
                && quizQuestion.Options.Distinct().Count() == 2
                && quizQuestion.CorrectOptions.Length == 1
                && (
                    quizQuestion.Options.Contains("True", StringComparer.OrdinalIgnoreCase)
                    || quizQuestion.Options.Contains("1")
                )
                && (
                    quizQuestion.Options.Contains("False", StringComparer.OrdinalIgnoreCase)
                    || quizQuestion.Options.Contains("0")
                )
                && (
                    quizQuestion
                        .CorrectOptions.First()
                        .Equals("True", StringComparison.OrdinalIgnoreCase)
                    || quizQuestion
                        .CorrectOptions.First()
                        .Equals("False", StringComparison.OrdinalIgnoreCase)
                    || quizQuestion.CorrectOptions.First() == "1"
                    || quizQuestion.CorrectOptions.First() == "0"
                );
        }

        private bool ValidateMCQOptions(BulkQuizQuestionViewModel quizQuestion)
        {
            return quizQuestion.Options.Length == 4
                && quizQuestion.Options.Distinct().Count() == 4
                && quizQuestion.CorrectOptions.Length == 1
                && quizQuestion.Options.Contains(quizQuestion.CorrectOptions[0]);
        }

        private bool ValidateMSQOptions(BulkQuizQuestionViewModel quizQuestion)
        {
            int optionCount = quizQuestion.Options.Length;
            int correctOptionCount = quizQuestion.CorrectOptions.Length;

            return (optionCount >= 5 && optionCount <= 8)
                && quizQuestion.Options.Distinct().Count() == optionCount
                && (correctOptionCount >= 2 && correctOptionCount <= 3)
                && quizQuestion.CorrectOptions.All(opt => quizQuestion.Options.Contains(opt));
        }

        private int GetMSQOptionCount(ExcelWorksheet worksheet, int row)
        {
            int count = 0;
            for (int i = 4; i <= 11; i++)
            {
                if (!string.IsNullOrEmpty(worksheet.Cells[row, i].Value?.ToString()))
                    count++;
                else
                    break;
            }
            return count;
        }

        private int GetMSQCorrectOptionCount(int optionCount)
        {
            if (optionCount >= 5 && optionCount <= 8)
                return 2;
            else if (optionCount >= 9 && optionCount <= 11)
                return 3;
            else
                return 0;
        }

        private string[] ExtractOptions(
            ExcelWorksheet worksheet,
            int row,
            int startColumn,
            int count,
            string questionType = null
        )
        {
            string[] options = new string[count];
            for (int i = 0; i < count; i++)
            {
                string option = worksheet.Cells[row, startColumn + i].Value?.ToString() ?? "";

                if (questionType == QuizQuestionTypes.TrueFalseQuestion)
                {
                    if (option == "1")
                        option = "True";
                    else if (option == "0")
                        option = "False";
                }

                options[i] = option;
            }
            return options;
        }
    }
}



//using FluentValidation;
//using LXP.Common.Constants;
//using LXP.Common.Entities;
//using LXP.Common.Validators;
//using LXP.Common.ViewModels.QuizQuestionViewModel;
//using LXP.Core.IServices;
//using LXP.Data.IRepository;
//using Microsoft.AspNetCore.Http;
//using OfficeOpenXml;

//namespace LXP.Core.Services
//{
//    public class BulkQuestionService : IBulkQuestionService
//    {
//        private readonly IBulkQuestionRepository _bulkQuestionRepository;
//        private readonly IQuizQuestionRepository _quizQuestionRepository;
//        private readonly IQuizRepository _quizRepository;
//        private readonly BulkQuizQuestionViewModelValidator _validator;

//        public BulkQuestionService(
//            IBulkQuestionRepository bulkQuestionRepository,
//            IQuizQuestionRepository quizQuestionRepository,
//            IQuizRepository quizRepository,
//            BulkQuizQuestionViewModelValidator validator
//        )
//        {
//            _bulkQuestionRepository = bulkQuestionRepository;
//            _quizQuestionRepository = quizQuestionRepository;
//            _quizRepository = quizRepository;
//            _validator = validator;
//        }

//        public async Task<object> ImportQuizDataAsync(IFormFile file, Guid quizId)
//        {
//            if (file == null || file.Length <= 0)
//                throw new ArgumentException("File is empty.");

//            using (var stream = new MemoryStream())
//            {
//                await file.CopyToAsync(stream);
//                stream.Position = 0;

//                using (ExcelPackage package = new ExcelPackage(stream))
//                {
//                    ExcelWorksheet worksheet = package.Workbook.Worksheets.FirstOrDefault();
//                    if (worksheet == null)
//                        throw new ArgumentException("Worksheet not found.");

//                    List<BulkQuizQuestionViewModel> quizQuestions =
//                        new List<BulkQuizQuestionViewModel>();

//                    // Loop through each row in the worksheet
//                    for (int row = 3; row <= worksheet.Dimension.End.Row; row++)
//                    {
//                        string type = worksheet.Cells[row, 2].Value?.ToString();

//                        if (type == QuizQuestionTypes.MultiChoiceQuestion ||
//                    type == QuizQuestionTypes.TrueFalseQuestion ||
//                    type == QuizQuestionTypes.MultiSelectQuestion)
//                        {
//                            BulkQuizQuestionViewModel quizQuestion = new BulkQuizQuestionViewModel
//                            {
//                                QuestionType = type,
//                                Question = worksheet.Cells[row, 3].Value.ToString(),
//                            };

//                            // Validate question type
//                            if (!ValidateQuestionType(quizQuestion))
//                                continue;

//                            if (type == QuizQuestionTypes.MultiChoiceQuestion)
//                            {
//                                quizQuestion.Options = ExtractOptions(worksheet, row, 4, 4, type);
//                                quizQuestion.CorrectOptions = ExtractOptions(worksheet, row, 12, 1, type);
//                                // Validate MCQ options
//                                if (!ValidateMCQOptions(quizQuestion))
//                                    continue;
//                            }
//                            else if (type == QuizQuestionTypes.TrueFalseQuestion)
//                            {
//                                quizQuestion.Options = ExtractOptions(worksheet, row, 4, 2, type);
//                                quizQuestion.CorrectOptions = ExtractOptions(worksheet, row, 12, 1, type);
//                                // Validate T/F options
//                                if (!ValidateTFOptions(quizQuestion))
//                                    continue;
//                            }
//                            else if (type == QuizQuestionTypes.MultiSelectQuestion)
//                            {
//                                int optionCount = GetMSQOptionCount(worksheet, row);
//                                quizQuestion.Options = ExtractOptions(
//                                    worksheet,
//                                    row,
//                                    4,
//                                    optionCount,
//                                    type
//                                );
//                                quizQuestion.CorrectOptions = ExtractOptions(
//                                    worksheet,
//                                    row,
//                                    12,
//                                    GetMSQCorrectOptionCount(optionCount),
//                                    type
//                                );
//                                // Validate MSQ options
//                                if (!ValidateMSQOptions(quizQuestion))
//                                    continue;
//                            }



//                            quizQuestions.Add(quizQuestion);
//                        }
//                    }

//                    // Loop through each question and add to repository
//                    foreach (var quizQuestion in quizQuestions)
//                    {
//                        // Validate using FluentValidation
//                        var validationResult = _validator.Validate(quizQuestion);
//                        if (!validationResult.IsValid)
//                            throw new ArgumentException(
//                                string.Join(
//                                    " ",
//                                    validationResult.Errors.Select(e => e.ErrorMessage)
//                                )
//                            );

//                        // Get the next available question number
//                        int nextQuestionNo = await _quizQuestionRepository.GetNextQuestionNoAsync(
//                            quizId
//                        );

//                        // Add question to the repository
//                        QuizQuestion questionEntity = new QuizQuestion
//                        {
//                            QuizId = quizId,
//                            QuestionNo = nextQuestionNo,
//                            QuestionType = quizQuestion.QuestionType,
//                            Question = quizQuestion.Question,
//                            CreatedBy = "Admin",
//                            CreatedAt = DateTime.UtcNow
//                        };

//                        // Save the question to get the QuizQuestionId
//                        await _bulkQuestionRepository.AddQuestionsAsync(
//                            new List<QuizQuestion> { questionEntity }
//                        );

//                        // Add options associated with the question
//                        List<QuestionOption> optionEntities = new List<QuestionOption>();
//                        for (int i = 0; i < quizQuestion.Options.Length; i++)
//                        {
//                            if (!string.IsNullOrEmpty(quizQuestion.Options[i]))
//                            {
//                                QuestionOption optionEntity = new QuestionOption
//                                {
//                                    QuizQuestionId = questionEntity.QuizQuestionId,
//                                    Option = quizQuestion.Options[i],
//                                    IsCorrect = quizQuestion.CorrectOptions.Contains(
//                                        quizQuestion.Options[i]
//                                    ),
//                                    CreatedAt = DateTime.UtcNow,
//                                    CreatedBy = "Admin",
//                                    ModifiedBy = "Admin"
//                                };

//                                optionEntities.Add(optionEntity);
//                            }
//                        }

//                        await _bulkQuestionRepository.AddOptionsAsync(
//                            optionEntities,
//                            questionEntity.QuizQuestionId
//                        );
//                    }
//                    return quizQuestions;
//                }
//            }
//        }

//        // Validate question type
//        private bool ValidateQuestionType(BulkQuizQuestionViewModel quizQuestion)
//        {
//            return quizQuestion.QuestionType == QuizQuestionTypes.MultiChoiceQuestion
//                || quizQuestion.QuestionType == QuizQuestionTypes.TrueFalseQuestion
//                || quizQuestion.QuestionType == QuizQuestionTypes.MultiSelectQuestion;
//        }


//        // Validate T/F options
//        private bool ValidateTFOptions(BulkQuizQuestionViewModel quizQuestion)
//        {
//            return quizQuestion.Options.Length == 2
//                && !string.IsNullOrEmpty(quizQuestion.Options[0])
//                && !string.IsNullOrEmpty(quizQuestion.Options[1])
//                && !quizQuestion
//                    .Options[0]
//                    .Equals(quizQuestion.Options[1], StringComparison.OrdinalIgnoreCase)
//                && (
//                    quizQuestion.CorrectOptions.Length == 1
//                    && (
//                        quizQuestion
//                            .CorrectOptions[0]
//                            .Equals("true", StringComparison.OrdinalIgnoreCase)
//                        || quizQuestion
//                            .CorrectOptions[0]
//                            .Equals("false", StringComparison.OrdinalIgnoreCase)
//                    )
//                );
//        }

//        // Validate MCQ options
//        private bool ValidateMCQOptions(BulkQuizQuestionViewModel quizQuestion)
//        {
//            return quizQuestion.Options.Length == 4
//                && quizQuestion.Options.Distinct().Count() == 4
//                && quizQuestion.CorrectOptions.Length == 1
//                && quizQuestion.Options.Contains(quizQuestion.CorrectOptions[0]);
//        }

//        // Validate MSQ options
//        private bool ValidateMSQOptions(BulkQuizQuestionViewModel quizQuestion)
//        {
//            int optionCount = quizQuestion.Options.Length;
//            int correctOptionCount = quizQuestion.CorrectOptions.Length;

//            return (optionCount >= 4 && optionCount <= 11)
//                && quizQuestion.Options.Distinct().Count() == optionCount
//                && (correctOptionCount >= 2 && correctOptionCount <= 3)
//                && quizQuestion.CorrectOptions.All(opt => quizQuestion.Options.Contains(opt));
//        }

//        // Get the count of MSQ options based on the filled rows
//        private int GetMSQOptionCount(ExcelWorksheet worksheet, int row)
//        {
//            int count = 0;
//            for (int i = 4; i <= 11; i++)
//            {
//                if (!string.IsNullOrEmpty(worksheet.Cells[row, i].Value?.ToString()))
//                    count++;
//                else
//                    break;
//            }
//            return count;
//        }

//        // Get the count of MSQ correct options based on the total option count
//        private int GetMSQCorrectOptionCount(int optionCount)
//        {
//            if (optionCount >= 5 && optionCount <= 8)
//                return 2;
//            else if (optionCount >= 9 && optionCount <= 11)
//                return 3;
//            else
//                return 0;
//        }


//        private string[] ExtractOptions(
//    ExcelWorksheet worksheet,
//    int row,
//    int startColumn,
//    int count,
//    string questionType = null
//)
//        {
//            string[] options = new string[count];
//            for (int i = 0; i < count; i++)
//            {
//                string option = worksheet.Cells[row, startColumn + i].Value?.ToString() ?? "";

//                if (questionType == QuizQuestionTypes.TrueFalseQuestion)
//                {
//                    if (option == "1") option = "True";
//                    else if (option == "0") option = "False";
//                }

//                options[i] = option;
//            }
//            return options;
//        }


//    }
//}

// Extract options from Excel worksheet
//private string[] ExtractOptions(
//    ExcelWorksheet worksheet,
//    int row,
//    int startColumn,
//    int count
//)
//{
//    string[] options = new string[count];
//    for (int i = 0; i < count; i++)
//    {
//        string option = worksheet.Cells[row, startColumn + i].Value?.ToString() ?? "";
//        options[i] = option;
//    }
//    return options;
//}

//if (type == QuizQuestionTypes.MultiChoiceQuestion)
//{
//    quizQuestion.Options = ExtractOptions(worksheet, row, 4, 4);
//    quizQuestion.CorrectOptions = ExtractOptions(worksheet, row, 12, 1);
//    // Validate MCQ options
//    if (!ValidateMCQOptions(quizQuestion))
//        continue;
//}
//else if (type == QuizQuestionTypes.TrueFalseQuestion)
//{
//    quizQuestion.Options = ExtractOptions(worksheet, row, 4, 2);
//    quizQuestion.CorrectOptions = ExtractOptions(worksheet, row, 12, 1);
//    // Validate T/F options
//    if (!ValidateTFOptions(quizQuestion))
//        continue;
//}
//else if (type == QuizQuestionTypes.MultiSelectQuestion)
//{
//    int optionCount = GetMSQOptionCount(worksheet, row);
//    quizQuestion.Options = ExtractOptions(
//        worksheet,
//        row,
//        4,
//        optionCount
//    );
//    quizQuestion.CorrectOptions = ExtractOptions(
//        worksheet,
//        row,
//        12,
//        GetMSQCorrectOptionCount(optionCount)
//    );
//    // Validate MSQ options
//    if (!ValidateMSQOptions(quizQuestion))
//        continue;
//}
